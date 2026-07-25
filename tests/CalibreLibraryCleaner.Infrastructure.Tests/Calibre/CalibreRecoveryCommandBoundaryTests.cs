using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Calibre;

[Collection(ProcessEnvironmentGroup.Name)]
public sealed class CalibreRecoveryCommandBoundaryTests
{
    [Fact]
    public async Task QualifiedRecoveryCommandsUseArgumentTokensAndNeverUseShellOrPermanentRemoval()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string log = Path.Combine(temporary.Path, "recovery-arguments.jsonl");
        executable.SetLogPath(log);
        string library = Path.Combine(temporary.Path, "library with spaces & metacharacters;");
        string backup = Path.Combine(temporary.Path, "verified original", "book.pdf");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        byte[] formatBytes = "original-format"u8.ToArray();
        File.WriteAllBytes(backup, formatBytes);
        using ServiceProvider provider = Provider(executable);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        RecoveryCapabilityProfile profile = provider
            .GetRequiredService<ICalibreExecutionProfileProvider>()
            .EvaluateRecoveryProfile(tool);
        IRecoveryCalibreGateway gateway = provider.GetRequiredService<IRecoveryCalibreGateway>();

        (await gateway.CreateEmptyRecordAsync(new(tool, profile, library,
            "Title & literal", ["Author; literal"], "Author"), CancellationToken.None))
            .IsTransportSuccess.Should().BeTrue();
        (await gateway.SetMetadataFieldAsync(new(tool, profile, library, new(42),
            RecoveryCalibreMetadataField.Identifiers, ["isbn:123", "doi:literal&value"]),
            CancellationToken.None)).IsTransportSuccess.Should().BeTrue();
        (await gateway.AddOrReplaceFormatAsync(new(tool, profile, library, new(42),
            "PDF", backup, Fingerprint(formatBytes), false),
            CancellationToken.None)).IsTransportSuccess.Should().BeTrue();
        (await gateway.RemoveFormatAsync(new(tool, profile, library, new(1),
            "PDF", Fingerprint(formatBytes)), CancellationToken.None))
            .IsTransportSuccess.Should().BeTrue();
        (await gateway.RemoveRecordAsync(new(tool, profile, library, new(2)),
            CancellationToken.None)).IsTransportSuccess.Should().BeTrue();

        string[][] calls = File.ReadLines(log)
            .Select(value => JsonSerializer.Deserialize<string[]>(value)!).ToArray();
        calls.Should().HaveCount(5);
        calls[0].Should().Equal("--with-library", library, "add", "--empty",
            "--title", "Title & literal", "--authors", "Author; literal");
        calls[1].Should().Equal("--with-library", library, "set_metadata", "42",
            "--field", "identifiers:isbn:123,doi:literal&value");
        calls[2].Should().Equal("--with-library", library, "add_format", "42", backup);
        calls[3].Should().Equal("--with-library", library, "remove_format", "1", "PDF");
        calls[4].Should().Equal("--with-library", library, "remove", "2");
        calls.SelectMany(value => value).Should().NotContain("--permanent");
    }

    [Fact]
    public async Task DisabledIndividualCapabilityIsNeverDispatched()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string log = Path.Combine(temporary.Path, "disabled.jsonl");
        executable.SetLogPath(log);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreExecutionOptions options = Options(executable) with
        {
            EnabledRecoveryCapabilities = new HashSet<RecoveryCapability>
            {
                RecoveryCapability.ExportCurrentRecord,
                RecoveryCapability.VerifyRestoredContent,
            },
        };
        using ServiceProvider provider = Provider(executable, options);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        RecoveryCapabilityProfile profile = provider
            .GetRequiredService<ICalibreExecutionProfileProvider>()
            .EvaluateRecoveryProfile(tool);

        RecoveryCalibreCommandResult result = await provider.GetRequiredService<IRecoveryCalibreGateway>()
            .RemoveRecordAsync(new(tool, profile, library, new(2)), CancellationToken.None);

        result.IsTransportSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("CALIBRE_RECOVERY_CAPABILITY_DISABLED");
        File.Exists(log).Should().BeFalse();
    }

    [Fact]
    public async Task UndocumentedCoverRestoreCannotBeEnabledByConfiguration()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        using ServiceProvider provider = Provider(executable);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;

        RecoveryCapabilityStatus cover = provider
            .GetRequiredService<ICalibreExecutionProfileProvider>()
            .EvaluateRecoveryProfile(tool).Capabilities.Single(value =>
                value.Capability == RecoveryCapability.RestoreCover);

        cover.Documented.Should().BeFalse();
        cover.IsDispatchable.Should().BeFalse();
    }

    private static ServiceProvider Provider(
        ControlledCalibreExecutable executable,
        CalibreExecutionOptions? options = null)
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
        IsValidatedRecoveryProfileEnabled = true,
        EnabledRecoveryCapabilities = Enum.GetValues<RecoveryCapability>().ToHashSet(),
        ProbeTimeout = TimeSpan.FromSeconds(5),
        ReadOnlyCommandTimeout = TimeSpan.FromSeconds(5),
    };

    private static FormatFileFingerprint Fingerprint(byte[] bytes) => new(
        bytes.LongLength,
        new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant()));
}
