using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

[Trait("Category", "OptInRealCalibre")]
[Collection(ProcessEnvironmentGroup.Name)]
public sealed class RealCalibreCompatibilityTests
{
    [RealCalibreFact]
    public async Task CompatibleCalibre9ProfileMutatesOnlyCallerMarkedDisposableLibrary()
    {
        string? executable = Environment.GetEnvironmentVariable("CALIBRE_TEST_EXE");
        string? parent = Environment.GetEnvironmentVariable("CALIBRE_TEST_ROOT");
        executable.Should().NotBeNullOrWhiteSpace("both opt-in variables are mandatory");
        parent.Should().NotBeNullOrWhiteSpace("both opt-in variables are mandatory");
        string canonicalExecutable = Path.GetFullPath(executable!);
        string canonicalParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent!));
        File.Exists(canonicalExecutable).Should().BeTrue("an explicitly supplied calibredb executable is required");
        Directory.Exists(canonicalParent).Should().BeTrue("an explicitly supplied disposable parent is required");
        IsPhysicalPath(canonicalParent).Should().BeTrue(
            "the disposable parent and every ancestor must be physical");
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        (canonicalParent + Path.DirectorySeparatorChar).Should().StartWith(tempRoot, "real-Calibre tests are restricted to the operating-system temporary root");
        string marker = Path.Combine(canonicalParent,
            ".calibre-library-cleaner-disposable-test-root");
        File.Exists(marker).Should().BeTrue(
            "the caller must place an explicit disposable-root marker");
        IsPhysicalPath(marker).Should().BeTrue(
            "the disposable marker must be a physical regular file");
        File.Exists(Path.Combine(canonicalParent, "metadata.db")).Should().BeFalse("a supplied Calibre library is never accepted as a test parent");
        string testRoot = Path.Combine(canonicalParent,
            $"calibre-integration-{Guid.NewGuid():N}");
        string canonicalTestRoot = Path.GetFullPath(testRoot);
        canonicalTestRoot.Should().StartWith(
            canonicalParent + Path.DirectorySeparatorChar);
        Directory.CreateDirectory(canonicalTestRoot);
        IsPhysicalPath(canonicalTestRoot).Should().BeTrue();
        string library = Path.Combine(canonicalTestRoot, "library");
        string config = Path.Combine(canonicalTestRoot, "config");
        string external = Path.Combine(canonicalTestRoot, "external");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(external);
        IsPhysicalPath(config).Should().BeTrue();
        IsPhysicalPath(external).Should().BeTrue();
        try
        {
            string first = Path.Combine(external, "first.txt");
            string second = Path.Combine(external, "second.txt");
            await File.WriteAllTextAsync(first, "first disposable record");
            await File.WriteAllTextAsync(second, "second disposable record");
            (await RunInitializationAsync(canonicalExecutable, config, ["--with-library", library, "add", first]))
                .Should().Be(0);
            IsPhysicalPath(library).Should().BeTrue();
            (await RunInitializationAsync(canonicalExecutable, config, ["--with-library", library, "add", second]))
                .Should().Be(0);

            using ServiceProvider provider = Provider(canonicalExecutable, config);
            CalibreToolDiscoveryResult discovery = await provider.GetRequiredService<ICalibreToolDiscovery>()
                .DiscoverAndProbeAsync(library, CancellationToken.None);
            discovery.IsSuccess.Should().BeTrue("the opt-in executable must be a capability-compatible Calibre 9.x release");
            CalibreToolDescriptor tool = discovery.Tool!;
            ICalibreCommandGateway gateway = provider.GetRequiredService<ICalibreCommandGateway>();
            string export = Path.Combine(external, "export-1");
            Directory.CreateDirectory(export);
            (await gateway.ExportRecordAsync(new(tool, library, new(1), export), CancellationToken.None)).IsSuccess.Should().BeTrue();
            Directory.EnumerateFiles(export, "*.opf", SearchOption.AllDirectories).Should().ContainSingle();

            string pdf = Path.Combine(external, "retained.pdf");
            await File.WriteAllBytesAsync(pdf, "first-pdf"u8.ToArray());
            Sha256Digest firstPdfDigest = Sha256("first-pdf"u8.ToArray());
            (await gateway.AddOrReplaceFormatAsync(new(tool, library, new(1), "PDF", pdf,
                new(new FileInfo(pdf).Length, firstPdfDigest)), CancellationToken.None)).IsSuccess.Should().BeTrue();
            (await File.ReadAllBytesAsync(pdf)).Should().Equal(
                "first-pdf"u8.ToArray());
            LibraryScanOutcome added = await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None);
            added.IsSuccess.Should().BeTrue();
            added.Snapshot!.Books.Single(value => value.Id == new CalibreBookId(1)).Formats
                .Should().Contain(value => value.Format == "PDF"
                    && value.Fingerprint!.Sha256 == firstPdfDigest);

            await File.WriteAllBytesAsync(pdf, "replacement-pdf"u8.ToArray());
            Sha256Digest replacementPdfDigest = Sha256("replacement-pdf"u8.ToArray());
            (await gateway.AddOrReplaceFormatAsync(new(tool, library, new(1), "PDF", pdf,
                new(new FileInfo(pdf).Length, replacementPdfDigest)), CancellationToken.None)).IsSuccess.Should().BeTrue();
            (await File.ReadAllBytesAsync(pdf)).Should().Equal(
                "replacement-pdf"u8.ToArray());
            LibraryScanOutcome replaced = await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None);
            replaced.Snapshot!.Books.Single(value => value.Id == new CalibreBookId(1)).Formats
                .Should().Contain(value => value.Format == "PDF"
                    && value.Fingerprint!.Sha256 == replacementPdfDigest);

            (await gateway.RemoveFormatAsync(new(tool, library, new(1), "PDF"), CancellationToken.None)).IsSuccess.Should().BeTrue();
            LibraryScanOutcome formatRemoved = await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None);
            formatRemoved.IsSuccess.Should().BeTrue();
            formatRemoved.Snapshot!.Books.Single(value => value.Id == new CalibreBookId(1)).Formats
                .Should().NotContain(value => value.Format == "PDF");

            (await gateway.RemoveRecordAsync(new(tool, library, new(2)), CancellationToken.None)).IsSuccess.Should().BeTrue();
            LibraryScanOutcome removed = await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None);
            removed.IsSuccess.Should().BeTrue();
            removed.Snapshot!.Books.Should().ContainSingle(value => value.Id == new CalibreBookId(1));
        }
        finally
        {
            string deleteTarget = Path.GetFullPath(canonicalTestRoot);
            if (Directory.Exists(deleteTarget)
                && deleteTarget.StartsWith(
                    canonicalParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(marker)
                && IsPhysicalPath(marker)
                && IsPhysicalPath(deleteTarget)
                && IsPhysicalTree(deleteTarget))
                Directory.Delete(deleteTarget, recursive: true);
        }
    }

    private static async Task<int> RunInitializationAsync(string executable, string config, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = config,
        };
        start.Environment["CALIBRE_CONFIG_DIRECTORY"] = config;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The opt-in Calibre process did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        _ = await output;
        _ = await error;
        return process.ExitCode;
    }

    private static ServiceProvider Provider(string executable, string config)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new CalibreExecutionOptions
        {
            TrustedExecutablePath = executable,
            ControlledConfigDirectory = config,
            IsValidatedCompatibilityProfileEnabled = true,
        });
        services.AddSingleton(new LibraryAnalysisOptions());
        services.AddSingleton<EpubAssessmentEngine>();
        services.AddSingleton<AssessEpubFormatsUseCase>();
        services.AddSingleton<ConsolidationRecommendationPolicy>();
        services.AddSingleton<GenerateConsolidationRecommendationsUseCase>();
        services.AddSingleton<ScanLibraryUseCase>();
        return services.BuildServiceProvider();
    }

    private static ScanLibraryUseCase Scanner(ServiceProvider provider) => provider.GetRequiredService<ScanLibraryUseCase>();
    private static Sha256Digest Sha256(byte[] bytes) => new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());

    private static bool IsPhysicalPath(string path)
    {
        try
        {
            FileSystemInfo? current = File.Exists(path)
                ? new FileInfo(Path.GetFullPath(path))
                : new DirectoryInfo(Path.GetFullPath(path));
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                current = current switch
                {
                    FileInfo file => file.Directory,
                    DirectoryInfo directory => directory.Parent,
                    _ => null,
                };
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
                   or UnauthorizedAccessException or ArgumentException
                   or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsPhysicalTree(string root)
    {
        try
        {
            Stack<string> pending = new();
            pending.Push(Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return false;
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                             directory, "*", SearchOption.TopDirectoryOnly))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return false;
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
                   or UnauthorizedAccessException or ArgumentException
                   or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class RealCalibreFactAttribute : FactAttribute
    {
        public RealCalibreFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CALIBRE_TEST_EXE"))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CALIBRE_TEST_ROOT")))
                Skip = "Set CALIBRE_TEST_EXE and CALIBRE_TEST_ROOT to run the opt-in real-Calibre compatibility test.";
        }
    }
}
