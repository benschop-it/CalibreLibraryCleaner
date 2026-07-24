using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

[Collection(ProcessEnvironmentGroup.Name)]
public sealed class CalibreCommandBoundaryTests : IDisposable
{
    public CalibreCommandBoundaryTests() => ClearEnvironment();
    public void Dispose() => ClearEnvironment();

    [Fact]
    public async Task ExactProfileDiscoveryProbesRequiredDocumentedCapabilities()
    {
        using ControlledCalibreExecutable executable = new();
        using ServiceProvider provider = Provider(executable);

        CalibreToolDiscoveryResult result = await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(DiscoveryRoot(executable), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Tool!.Identity.ProductVersion.Should().Be("9.11.0");
        result.Tool.Capabilities.Should().BeEquivalentTo(Enum.GetValues<CalibreExecutionCapability>());
    }

    [Fact]
    public async Task UnqualifiedRealCalibreProfileIsDisabledByDefault()
    {
        using ControlledCalibreExecutable executable = new();
        CalibreExecutionOptions options = Options(executable) with { IsValidatedCompatibilityProfileEnabled = false };
        using ServiceProvider provider = Provider(executable, options);

        CalibreToolDiscoveryResult result = await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(DiscoveryRoot(executable), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.CALIBRE_PROFILE_NOT_VALIDATED");
    }

    [Fact]
    public async Task UnknownVersionFailsClosedBeforeAnyMutationCapabilityIsReturned()
    {
        using ControlledCalibreExecutable executable = new();
        executable.SetVersion("9.12.0");
        using ServiceProvider provider = Provider(executable);

        CalibreToolDiscoveryResult result = await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(DiscoveryRoot(executable), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Tool.Should().BeNull();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.CALIBRE_VERSION_UNSUPPORTED");
    }

    [Fact]
    public async Task ConfigurationCannotEnableAnUnapprovedCompatibilityProfile()
    {
        using ControlledCalibreExecutable executable = new();
        CalibreExecutionOptions options = Options(executable) with
        {
            SupportedVersion = "9.12.0",
            CapabilityProfile = "calibredb/windows/9.12.0",
        };
        using ServiceProvider provider = Provider(executable, options);

        CalibreToolDiscoveryResult result = await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(DiscoveryRoot(executable), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.CALIBRE_PROFILE_CONFIGURATION_UNSUPPORTED");
    }

    [Fact]
    public async Task TypedCommandsUseExactArgumentTokensAndNeverRequestPermanentRemoval()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string log = Path.Combine(temporary.Path, "arguments.jsonl");
        executable.SetLogPath(log);
        using ServiceProvider provider = Provider(executable);
        ICalibreToolDiscovery discovery = provider.GetRequiredService<ICalibreToolDiscovery>();
        string library = Path.Combine(temporary.Path, "library with spaces & semicolon;");
        string export = Path.Combine(temporary.Path, "export");
        string backup = Path.Combine(temporary.Path, "backup $(literal)", "book & retained.pdf");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(export);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        byte[] backupBytes = "hello"u8.ToArray();
        File.WriteAllBytes(backup, backupBytes);
        CalibreToolDescriptor tool = (await discovery.DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        ICalibreCommandGateway gateway = provider.GetRequiredService<ICalibreCommandGateway>();

        (await gateway.ExportRecordAsync(new(tool, library, new(2), export), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await gateway.AddOrReplaceFormatAsync(new(tool, library, new(1), "PDF", backup,
            Fingerprint(backupBytes)), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await gateway.RemoveRecordAsync(new(tool, library, new(2)), CancellationToken.None)).IsSuccess.Should().BeTrue();

        string[][] calls = File.ReadLines(log).Select(value => JsonSerializer.Deserialize<string[]>(value)!).ToArray();
        calls.Should().HaveCount(3);
        calls[0].Should().Equal("--with-library", library, "export", "--dont-update-metadata", "--to-dir", export, "--single-dir", "2");
        calls[1].Should().Equal("--with-library", library, "add_format", "1", backup);
        calls[2].Should().Equal("--with-library", library, "remove", "2");
        calls.SelectMany(value => value).Should().NotContain("--permanent");
    }

    [Fact]
    public async Task NonzeroMutationExitIsReturnedWithoutRetry()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string log = Path.Combine(temporary.Path, "arguments.jsonl");
        executable.SetLogPath(log);
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        executable.SetExitCode(17);

        CalibreCommandResult result = await provider.GetRequiredService<ICalibreCommandGateway>()
            .RemoveRecordAsync(new(tool, library, new(2)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ExitCode.Should().Be(17);
        result.FailureCode.Should().Be("CALIBRE_PROCESS_NONZERO_EXIT");
        File.ReadLines(log).Should().ContainSingle("mutation commands are never retried");
    }

    [Fact]
    public async Task ReadOnlyCommandTimeoutIsBoundedAndReported()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        CalibreExecutionOptions options = Options(executable) with { ReadOnlyCommandTimeout = TimeSpan.FromMilliseconds(50) };
        using ServiceProvider provider = Provider(executable, options);
        string library = Path.Combine(temporary.Path, "library");
        string export = Path.Combine(temporary.Path, "export");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(export);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        executable.SetSleepMilliseconds(300);

        CalibreCommandResult result = await provider.GetRequiredService<ICalibreCommandGateway>()
            .ExportRecordAsync(new(tool, library, new(1), export), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("CALIBRE_PROCESS_TIMEOUT");
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ActiveMutatingProcessIsNotKilledWhenCallerCancelsAfterLaunch()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        string backup = Path.Combine(temporary.Path, "backup", "book.pdf");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        byte[] backupBytes = "hello"u8.ToArray();
        File.WriteAllBytes(backup, backupBytes);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        executable.SetSleepMilliseconds(300);
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(75);
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

        CalibreCommandResult result = await provider.GetRequiredService<ICalibreCommandGateway>()
            .AddOrReplaceFormatAsync(new(tool, library, new(1), "PDF", backup,
                Fingerprint(backupBytes)), cancellation.Token);

        result.IsSuccess.Should().BeTrue();
        elapsed.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task CapturedOutputIsBoundedAndSensitivePathsAreRedacted()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        CalibreExecutionOptions options = Options(executable) with { MaximumCapturedCharacters = 128 };
        using ServiceProvider provider = Provider(executable, options);
        string library = Path.Combine(temporary.Path, "library");
        string export = Path.Combine(temporary.Path, "export");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(export);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        executable.SetStandardOutputCharacters(10000);

        CalibreCommandResult result = await provider.GetRequiredService<ICalibreCommandGateway>()
            .ExportRecordAsync(new(tool, library, new(1), export), CancellationToken.None);

        result.SanitizedStandardOutput.Length.Should().BeLessThanOrEqualTo(128);
        result.SanitizedArguments.Should().NotContain(library).And.NotContain(export);
        result.SanitizedArguments.Should().Contain("<path>");
    }

    [Fact]
    public async Task CalibreProcessDoesNotInheritArbitraryParentEnvironmentVariables()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        string export = Path.Combine(temporary.Path, "export");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(export);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        const string variable = "CLC_SENSITIVE_PARENT_VALUE";
        Environment.SetEnvironmentVariable(variable, "must-not-reach-calibre");
        executable.SetEnvironmentProbe(variable);
        try
        {
            CalibreCommandResult result = await provider.GetRequiredService<ICalibreCommandGateway>()
                .ExportRecordAsync(new(tool, library, new(1), export), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.SanitizedStandardOutput.Should().Contain("<missing>")
                .And.NotContain("must-not-reach-calibre");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task VerifiedBackupCannotBeReplacedWhileMutatingProcessUsesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        string backup = Path.Combine(temporary.Path, "backup", "book.pdf");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        byte[] bytes = "verified"u8.ToArray();
        File.WriteAllBytes(backup, bytes);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        executable.SetSleepMilliseconds(400);

        Task<CalibreCommandResult> running = provider.GetRequiredService<ICalibreCommandGateway>()
            .AddOrReplaceFormatAsync(new(tool, library, new(1), "PDF", backup, Fingerprint(bytes)),
                CancellationToken.None);
        await Task.Delay(100);

        Action replace = () => File.WriteAllBytes(backup, "tampered"u8.ToArray());
        replace.Should().Throw<IOException>();
        (await running).IsSuccess.Should().BeTrue();
    }

    private static ServiceProvider Provider(ControlledCalibreExecutable executable, CalibreExecutionOptions? options = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(options ?? Options(executable));
        return services.BuildServiceProvider();
    }

    private static CalibreExecutionOptions Options(ControlledCalibreExecutable executable) => new()
    {
        TrustedExecutablePath = executable.ExecutablePath,
        ControlledConfigDirectory = executable.ConfigDirectory,
        IsValidatedCompatibilityProfileEnabled = true,
        ProbeTimeout = TimeSpan.FromSeconds(5),
        ReadOnlyCommandTimeout = TimeSpan.FromSeconds(5),
    };

    private static string DiscoveryRoot(ControlledCalibreExecutable executable) =>
        Path.Combine(executable.Root, "synthetic-library");

    private static FormatFileFingerprint Fingerprint(byte[] bytes) => new(
        bytes.LongLength,
        new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));

    private static void ClearEnvironment()
    {
        foreach (string name in new[] { "CLC_TEST_CALIBRE_VERSION", "CLC_TEST_CALIBRE_LOG", "CLC_TEST_CALIBRE_SLEEP_MS", "CLC_TEST_CALIBRE_STDOUT_CHARS", "CLC_TEST_CALIBRE_EXIT", "CLC_TEST_CALIBRE_EXPORT_SOURCE" })
            Environment.SetEnvironmentVariable(name, null);
    }
}
