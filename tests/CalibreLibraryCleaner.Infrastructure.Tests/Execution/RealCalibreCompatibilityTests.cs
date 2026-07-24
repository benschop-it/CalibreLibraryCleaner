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
    public async Task ExactCalibreProfileMutatesOnlyCallerMarkedDisposableLibrary()
    {
        string? executable = Environment.GetEnvironmentVariable("CALIBRE_TEST_EXE");
        string? parent = Environment.GetEnvironmentVariable("CALIBRE_TEST_ROOT");
        executable.Should().NotBeNullOrWhiteSpace("both opt-in variables are mandatory");
        parent.Should().NotBeNullOrWhiteSpace("both opt-in variables are mandatory");
        string canonicalExecutable = Path.GetFullPath(executable!);
        string canonicalParent = Path.GetFullPath(parent!);
        File.Exists(canonicalExecutable).Should().BeTrue("an explicitly supplied calibredb executable is required");
        Directory.Exists(canonicalParent).Should().BeTrue("an explicitly supplied disposable parent is required");
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        canonicalParent.Should().StartWith(tempRoot, "real-Calibre tests are restricted to the operating-system temporary root");
        File.Exists(Path.Combine(canonicalParent, ".calibre-library-cleaner-disposable-test-root")).Should().BeTrue(
            "the caller must place an explicit disposable-root marker");
        File.Exists(Path.Combine(canonicalParent, "metadata.db")).Should().BeFalse("a supplied Calibre library is never accepted as a test parent");
        string testRoot = Path.Combine(canonicalParent, $"calibre-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        string library = Path.Combine(testRoot, "library");
        string config = Path.Combine(testRoot, "config");
        string external = Path.Combine(testRoot, "external");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(external);
        try
        {
            string first = Path.Combine(external, "first.txt");
            string second = Path.Combine(external, "second.txt");
            await File.WriteAllTextAsync(first, "first disposable record");
            await File.WriteAllTextAsync(second, "second disposable record");
            (await RunInitializationAsync(canonicalExecutable, config, ["--with-library", library, "add", first]))
                .Should().Be(0);
            (await RunInitializationAsync(canonicalExecutable, config, ["--with-library", library, "add", second]))
                .Should().Be(0);

            using ServiceProvider provider = Provider(canonicalExecutable, config);
            CalibreToolDiscoveryResult discovery = await provider.GetRequiredService<ICalibreToolDiscovery>()
                .DiscoverAndProbeAsync(library, CancellationToken.None);
            discovery.IsSuccess.Should().BeTrue("the opt-in executable must be exact supported Calibre 9.11.0");
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
            LibraryScanOutcome added = await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None);
            added.IsSuccess.Should().BeTrue();
            added.Snapshot!.Books.Single(value => value.Id == new CalibreBookId(1)).Formats
                .Should().Contain(value => value.Format == "PDF"
                    && value.Fingerprint!.Sha256 == firstPdfDigest);

            await File.WriteAllBytesAsync(pdf, "replacement-pdf"u8.ToArray());
            Sha256Digest replacementPdfDigest = Sha256("replacement-pdf"u8.ToArray());
            (await gateway.AddOrReplaceFormatAsync(new(tool, library, new(1), "PDF", pdf,
                new(new FileInfo(pdf).Length, replacementPdfDigest)), CancellationToken.None)).IsSuccess.Should().BeTrue();
            LibraryScanOutcome replaced = await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None);
            replaced.Snapshot!.Books.Single(value => value.Id == new CalibreBookId(1)).Formats
                .Should().Contain(value => value.Format == "PDF"
                    && value.Fingerprint!.Sha256 == replacementPdfDigest);

            (await gateway.RemoveRecordAsync(new(tool, library, new(2)), CancellationToken.None)).IsSuccess.Should().BeTrue();
            LibraryScanOutcome removed = await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None);
            removed.IsSuccess.Should().BeTrue();
            removed.Snapshot!.Books.Should().ContainSingle(value => value.Id == new CalibreBookId(1));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
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
