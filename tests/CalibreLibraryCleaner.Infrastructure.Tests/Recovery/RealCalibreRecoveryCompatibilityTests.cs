using System.Diagnostics;
using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recovery;

[Trait("Category", "OptInRealCalibreRecovery")]
[Collection(ProcessEnvironmentGroup.Name)]
public sealed class RealCalibreRecoveryCompatibilityTests
{
    [RealCalibreRecoveryFact]
    public async Task ExactProfileQualifiesEachClosedRecoveryCommandAgainstADisposableLibrary()
    {
        string? executable = Environment.GetEnvironmentVariable("CALIBRE_TEST_EXE");
        string? parent = Environment.GetEnvironmentVariable("CALIBRE_TEST_ROOT");
        executable.Should().NotBeNullOrWhiteSpace("both opt-in variables are mandatory");
        parent.Should().NotBeNullOrWhiteSpace("both opt-in variables are mandatory");
        string canonicalExecutable = Path.GetFullPath(executable!);
        string canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent!));
        File.Exists(canonicalExecutable).Should().BeTrue(
            "an explicitly supplied calibredb executable is required");
        Directory.Exists(canonicalParent).Should().BeTrue(
            "an explicitly supplied disposable parent is required");
        IsPhysicalPath(canonicalParent)
            .Should().BeTrue("the disposable parent and every ancestor must be physical");
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            + Path.DirectorySeparatorChar;
        (canonicalParent + Path.DirectorySeparatorChar).Should().StartWith(tempRoot,
            "real-Calibre tests are restricted to the operating-system temporary root");
        string marker = Path.Combine(canonicalParent,
            ".calibre-library-cleaner-disposable-test-root");
        File.Exists(marker).Should().BeTrue(
            "the caller must place an explicit disposable-root marker");
        IsPhysicalPath(marker)
            .Should().BeTrue("the disposable marker must be a physical regular file");
        File.Exists(Path.Combine(canonicalParent, "metadata.db")).Should().BeFalse(
            "a supplied Calibre library is never accepted as a test parent");

        string testRoot = Path.Combine(canonicalParent,
            $"calibre-recovery-qualification-{Guid.NewGuid():N}");
        string canonicalTestRoot = Path.GetFullPath(testRoot);
        canonicalTestRoot.Should().StartWith(canonicalParent + Path.DirectorySeparatorChar);
        Directory.CreateDirectory(canonicalTestRoot);
        IsPhysicalPath(canonicalTestRoot)
            .Should().BeTrue("the generated test root must remain physical");
        string library = Path.Combine(canonicalTestRoot, "library");
        string config = Path.Combine(canonicalTestRoot, "config");
        string external = Path.Combine(canonicalTestRoot, "external");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(external);
        IsPhysicalPath(config).Should().BeTrue();
        IsPhysicalPath(external).Should().BeTrue();
        try
        {
            string seed = Path.Combine(external, "seed.txt");
            await File.WriteAllTextAsync(seed, "recovery qualification seed");
            (await RunInitializationAsync(canonicalExecutable, config,
                ["--with-library", library, "add", seed])).Should().Be(0);
            IsPhysicalPath(library).Should().BeTrue(
                "the generated disposable Calibre library must remain physical");

            using ServiceProvider provider = Provider(canonicalExecutable, config);
            CalibreToolDiscoveryResult discovery = await provider
                .GetRequiredService<ICalibreToolDiscovery>()
                .DiscoverAndProbeAsync(library, CancellationToken.None);
            discovery.IsSuccess.Should().BeTrue(
                "the supplied executable must be the exact supported Calibre 9.11.0 profile");
            CalibreToolDescriptor tool = discovery.Tool!;
            RecoveryCapabilityProfile profile = provider
                .GetRequiredService<ICalibreExecutionProfileProvider>()
                .EvaluateRecoveryProfile(tool);
            profile.Capabilities.Where(value =>
                    value.Capability != RecoveryCapability.RestoreCover)
                .Should().OnlyContain(value =>
                value.Documented && value.ClosedMappingTested
                && value.RealCalibreQualified && value.Enabled);
            profile.Capabilities.Single(value =>
                    value.Capability == RecoveryCapability.RestoreCover)
                .IsDispatchable.Should().BeFalse();

            ICalibreCommandGateway executionGateway =
                provider.GetRequiredService<ICalibreCommandGateway>();
            IRecoveryCalibreGateway recoveryGateway =
                provider.GetRequiredService<IRecoveryCalibreGateway>();

            string export = Path.Combine(external, "current-export");
            Directory.CreateDirectory(export);
            (await executionGateway.ExportRecordAsync(
                new(tool, library, new(1), export), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
            Directory.EnumerateFiles(export, "*.opf", SearchOption.AllDirectories)
                .Should().ContainSingle();

            RecoveryCalibreCommandResult created = await recoveryGateway
                .CreateEmptyRecordAsync(new(tool, profile, library,
                    "Recovered title", ["Recovery Author"], "Author, Recovery"),
                    CancellationToken.None);
            created.IsTransportSuccess.Should().BeTrue();
            created.CreatedRecordId.Should().NotBeNull(
                "record creation must expose an unambiguous numeric ID");
            CalibreBookId createdId = created.CreatedRecordId!.Value;

            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.Title, ["Restored title"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.Authors, ["First Author", "Second Author"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.AuthorSort, ["Author, First"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.Publisher, ["Recovery Publisher"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.PublicationDate,
                ["2020-01-02T00:00:00+00:00"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.Languages, ["eng", "deu"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.Identifiers,
                ["isbn:9780000000002", "recovery:test"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.Series, ["Recovery Series"]);
            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.SeriesIndex, ["2.5"]);

            LibraryScanOutcome metadataScan = await Scanner(provider)
                .ExecuteAsync(library, null, CancellationToken.None);
            metadataScan.IsSuccess.Should().BeTrue();
            CalibreBook recovered = metadataScan.Snapshot!.Books.Single(value =>
                value.Id == createdId);
            recovered.Title.Should().Be("Restored title");
            recovered.Authors.Select(value => value.Name)
                .Should().Equal("First Author", "Second Author");
            recovered.AuthorSort.Should().Be("Author, First");
            recovered.PublicationMetadata.Publisher.Should().Be("Recovery Publisher");
            recovered.PublicationMetadata.PublicationDate?.Date
                .Should().Be(new DateTime(2020, 1, 2));
            recovered.PublicationMetadata.Languages.Should().Equal("eng", "deu");
            recovered.Identifiers.Should().Contain(value =>
                value.Type == "isbn" && value.Value == "9780000000002");
            recovered.Identifiers.Should().Contain(value =>
                value.Type == "recovery" && value.Value == "test");
            recovered.PublicationMetadata.Series.Should().Be("Recovery Series");
            recovered.PublicationMetadata.SeriesIndex.Should().Be(2.5m);

            await SetAsync(recoveryGateway, tool, profile, library, createdId,
                RecoveryCalibreMetadataField.Publisher, [""]);
            LibraryScanOutcome cleared = await Scanner(provider)
                .ExecuteAsync(library, null, CancellationToken.None);
            cleared.Snapshot!.Books.Single(value => value.Id == createdId)
                .PublicationMetadata.Publisher.Should().BeNullOrEmpty(
                    "empty field restoration must have a qualified semantic result");

            string format = Path.Combine(external, "restored.pdf");
            byte[] originalBytes = "original recovery format"u8.ToArray();
            await File.WriteAllBytesAsync(format, originalBytes);
            FormatFileFingerprint originalFingerprint = Fingerprint(originalBytes);
            (await recoveryGateway.AddOrReplaceFormatAsync(new(tool, profile, library,
                createdId, "PDF", format, originalFingerprint, false),
                CancellationToken.None)).IsTransportSuccess.Should().BeTrue();
            (await File.ReadAllBytesAsync(format)).Should().Equal(originalBytes,
                "a qualified add-format command must not modify its source backup");
            (await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None))
                .Snapshot!.Books.Single(value => value.Id == createdId).Formats
                .Should().Contain(value => value.Format == "PDF"
                    && value.Fingerprint == originalFingerprint);

            byte[] replacementBytes = "replacement recovery format"u8.ToArray();
            await File.WriteAllBytesAsync(format, replacementBytes);
            FormatFileFingerprint replacementFingerprint = Fingerprint(replacementBytes);
            (await recoveryGateway.AddOrReplaceFormatAsync(new(tool, profile, library,
                createdId, "PDF", format, replacementFingerprint, true),
                CancellationToken.None)).IsTransportSuccess.Should().BeTrue();
            (await File.ReadAllBytesAsync(format)).Should().Equal(replacementBytes,
                "a qualified replace-format command must not modify its source backup");
            (await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None))
                .Snapshot!.Books.Single(value => value.Id == createdId).Formats
                .Should().Contain(value => value.Format == "PDF"
                    && value.Fingerprint == replacementFingerprint);

            (await recoveryGateway.RemoveFormatAsync(new(tool, profile, library,
                createdId, "PDF", replacementFingerprint), CancellationToken.None))
                .IsTransportSuccess.Should().BeTrue();
            (await Scanner(provider).ExecuteAsync(library, null, CancellationToken.None))
                .Snapshot!.Books.Single(value => value.Id == createdId).Formats
                .Should().NotContain(value => value.Format == "PDF");

            (await recoveryGateway.RemoveRecordAsync(new(tool, profile, library, createdId),
                CancellationToken.None)).IsTransportSuccess.Should().BeTrue();
            LibraryScanOutcome finalScan = await Scanner(provider)
                .ExecuteAsync(library, null, CancellationToken.None);
            finalScan.IsSuccess.Should().BeTrue();
            finalScan.Snapshot!.Books.Should().ContainSingle(value =>
                value.Id == new CalibreBookId(1));
        }
        finally
        {
            string expectedPrefix = canonicalParent + Path.DirectorySeparatorChar;
            string deleteTarget = Path.GetFullPath(canonicalTestRoot);
            if (Directory.Exists(deleteTarget)
                && deleteTarget.StartsWith(expectedPrefix,
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(marker)
                && IsPhysicalPath(marker)
                && IsPhysicalPath(deleteTarget)
                && IsPhysicalTree(deleteTarget))
                Directory.Delete(deleteTarget, recursive: true);
        }
    }

    private static async Task SetAsync(
        IRecoveryCalibreGateway gateway,
        CalibreToolDescriptor tool,
        RecoveryCapabilityProfile profile,
        string library,
        CalibreBookId recordId,
        RecoveryCalibreMetadataField field,
        IReadOnlyList<string> values) =>
        (await gateway.SetMetadataFieldAsync(
            new(tool, profile, library, recordId, field, values),
            CancellationToken.None)).IsTransportSuccess.Should().BeTrue();

    private static async Task<int> RunInitializationAsync(
        string executable,
        string config,
        IReadOnlyList<string> arguments)
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
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "The opt-in Calibre process did not start.");
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
            IsValidatedRecoveryProfileEnabled = true,
            EnabledRecoveryCapabilities =
                Enum.GetValues<RecoveryCapability>().ToHashSet(),
        });
        services.AddSingleton(new LibraryAnalysisOptions());
        services.AddSingleton<EpubAssessmentEngine>();
        services.AddSingleton<AssessEpubFormatsUseCase>();
        services.AddSingleton<ConsolidationRecommendationPolicy>();
        services.AddSingleton<GenerateConsolidationRecommendationsUseCase>();
        services.AddSingleton<ScanLibraryUseCase>();
        return services.BuildServiceProvider();
    }

    private static ScanLibraryUseCase Scanner(ServiceProvider provider) =>
        provider.GetRequiredService<ScanLibraryUseCase>();

    private static FormatFileFingerprint Fingerprint(byte[] bytes) => new(
        bytes.LongLength,
        new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));

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

    private sealed class RealCalibreRecoveryFactAttribute : FactAttribute
    {
        public RealCalibreRecoveryFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CALIBRE_TEST_EXE"))
                && string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CALIBRE_TEST_ROOT")))
                Skip = "Set CALIBRE_TEST_EXE and CALIBRE_TEST_ROOT to run the opt-in exact-version recovery qualification.";
        }
    }
}
