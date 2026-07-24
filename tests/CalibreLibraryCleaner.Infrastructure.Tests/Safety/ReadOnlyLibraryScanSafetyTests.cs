using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Safety;

public sealed class ReadOnlyLibraryScanSafetyTests
{
    [Fact]
    public async Task CleanupPlanLifecycleAndExternalRoundTripDoNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book", ["Author"], ["Author"], [1, 2, 3]);
        library.AddMetadataBook(2, "BOOK", ["Author"], ["Different sort"], [1, 2, 3]);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        LibraryScanOutcome scan = await TestServices.CreateScanUseCase(provider)
            .ExecuteAsync(library.RootPath, null, CancellationToken.None);
        LibrarySnapshot analysis = scan.Snapshot!;
        ConsolidationRecommendation[] recommendations = (await new GenerateConsolidationRecommendationsUseCase(
            new ConsolidationRecommendationPolicy()).ExecuteAsync(analysis, null, CancellationToken.None)).ToArray();
        LibrarySnapshot snapshot = new(
            analysis.Identity, analysis.ScannedAt, analysis.Books, analysis.Findings,
            analysis.ExactBinaryDuplicateGroups, analysis.ExactMetadataDuplicateGroups, analysis.EpubAssessments, recommendations);
        ConsolidationRecommendation generated = recommendations.Single();
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted,
            provider.GetRequiredService<IClock>().GetUtcNow())).Reviewed!;
        CleanupPlan valid = new GenerateCleanupPlanUseCase(
            provider.GetRequiredService<ICleanupPlanIdGenerator>(),
            provider.GetRequiredService<IClock>()).Execute(snapshot, reviewed).Plan!;
        CleanupPlan approved = new ApproveCleanupPlanUseCase(provider.GetRequiredService<IClock>()).Execute(valid, snapshot, reviewed).Plan!;
        using TemporaryDirectory external = new();
        string artifact = Path.Combine(external.Path, "plan.cleanup-plan.json");
        ICleanupPlanStore store = provider.GetRequiredService<ICleanupPlanStore>();

        CleanupPlanStoreWriteResult write = await store.WriteAsync(approved, library.RootPath, artifact, CancellationToken.None);
        CleanupPlanStoreReadResult import = await store.ReadAsync(library.RootPath, artifact, CancellationToken.None);

        write.IsSuccess.Should().BeTrue();
        import.IsSuccess.Should().BeTrue();
        import.Plan!.State.Should().Be(CleanupPlanState.Approved);
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "*", SearchOption.AllDirectories)
            .Should().NotContain(path => path.EndsWith(".cleanup-plan.json", StringComparison.OrdinalIgnoreCase)
                || path.Contains(".tmp", StringComparison.OrdinalIgnoreCase)
                || path.Contains("backup", StringComparison.OrdinalIgnoreCase)
                || path.Contains("lock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidEpubAssessmentDoesNotChangeSyntheticLibrary()
    {
        using TemporaryDirectory fixtureDirectory = new();
        string epubPath = Path.Combine(fixtureDirectory.Path, "Valid.epub");
        SyntheticEpubBuilder.CreateValid(epubPath);
        using SyntheticCalibreLibrary library = new();
        library.AddSimpleBook(1, await File.ReadAllBytesAsync(epubPath));
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();

        LibraryScanOutcome outcome = await TestServices.CreateScanUseCase(provider)
            .ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.EpubAssessments.Should().ContainSingle(assessment => assessment.Score.HasValue);
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task ExactDuplicateAnalysisDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        byte[] content = [1, 2, 3, 4, 5];
        library.AddSimpleBook(1, content);
        library.AddSimpleBook(2, content);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().ContainSingle();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ExactMetadataDuplicateAnalysisDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book : One", ["Author"], ["Author"], [1, 2, 3]);
        library.AddMetadataBook(2, "book:one", ["Author"], ["Different sort"], [3, 2, 1]);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactMetadataDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactBinaryDuplicateGroups.Should().BeEmpty();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task CombinedBinaryAndMetadataDuplicateAnalysisDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        byte[] content = [1, 2, 3];
        library.AddMetadataBook(1, "Book", ["Author"], ["Author"], content);
        library.AddMetadataBook(2, "BOOK", ["Author"], ["Different sort"], content);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactMetadataDuplicateGroups.Should().ContainSingle();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task NoAuthorExclusionDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book", [], [], [1]);
        library.AddMetadataBook(2, "Book", [], [], [2]);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactMetadataDuplicateGroups.Should().BeEmpty();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationDuringMetadataGroupingDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book", ["Author"], ["Author"], [1]);
        library.AddMetadataBook(2, "Book", ["Author"], ["Author"], [2]);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(update =>
        {
            if (update.Phase == LibraryScanPhase.GroupingExactMetadataDuplicates)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = async () => await useCase.ExecuteAsync(library.RootPath, progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task InaccessibleFormatFindingDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        string formatPath = library.AddSimpleBook(1, [1, 2, 3]);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);
        LibraryScanOutcome outcome;
        await using (FileStream locked = new(formatPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);
        }

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.Findings.Should().ContainSingle(finding => finding.Code == "FORMAT_FILE_INACCESSIBLE");
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanDoesNotChangeAnythingInsideSyntheticLibrary(bool createExpectedFormat)
    {
        using SyntheticCalibreLibrary library = new();
        library.AddBook(createExpectedFormat);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(
            library.RootPath,
            null,
            CancellationToken.None);
        IReadOnlyList<LibraryEntryState> after = LibraryStateCapture.Capture(library.RootPath);

        outcome.IsSuccess.Should().BeTrue();
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
        if (!createExpectedFormat)
        {
            outcome.Snapshot!.Findings.Should().ContainSingle(finding => finding.Code == "FORMAT_FILE_MISSING");
        }
    }

    [Fact]
    public async Task MissingExpectedFileDoesNotRenameAlternateFormatFile()
    {
        using SyntheticCalibreLibrary library = new();
        string expected = library.AddBook(createFormatFile: false);
        string directory = Path.GetDirectoryName(expected)!;
        Directory.CreateDirectory(directory);
        string alternate = Path.Combine(directory, "Alternate.epub");
        await File.WriteAllBytesAsync(alternate, [1, 2, 3]);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(
            library.RootPath,
            null,
            CancellationToken.None);

        outcome.Snapshot!.Findings.Should().ContainSingle(finding => finding.Code == "FORMAT_FILE_MISSING");
        File.Exists(expected).Should().BeFalse();
        File.ReadAllBytes(alternate).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task CanceledReadDoesNotChangeAnythingInsideSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddBook();
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(update =>
        {
            if (update.Phase == LibraryScanPhase.ReadingBooks)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = async () => await useCase.ExecuteAsync(
            library.RootPath,
            progress,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task CancellationDuringHashingDoesNotChangeAnythingInsideSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        string formatPath = library.AddSimpleBook(1, [0]);
        await using (FileStream stream = new(formatPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(update =>
        {
            if (update.Phase == LibraryScanPhase.HashingFormats && update.CompletedUnits > 0)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = async () => await useCase.ExecuteAsync(library.RootPath, progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task SameSizeNonmatchAnalysisDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddSimpleBook(1, [1, 2, 3]);
        library.AddSimpleBook(2, [3, 2, 1]);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().BeEmpty();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task FatalHashFailureDoesNotChangeSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddSimpleBook(1, [1, 2, 3]);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = new(
            provider.GetRequiredService<ILibraryPathResolver>(),
            provider.GetRequiredService<ICalibreMetadataReader>(),
            new ThrowingHasher(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<LibraryAnalysisOptions>());

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Error!.Code.Should().Be(LibraryErrorCode.HashingFailed);
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private sealed class InlineProgress(Action<LibraryScanProgress> report) : IProgress<LibraryScanProgress>
    {
        public void Report(LibraryScanProgress value) => report(value);
    }

    private sealed class ThrowingHasher : IFormatFileHasher
    {
        public Task<IReadOnlyList<FormatHashResult>> HashAsync(
            IReadOnlyList<FormatHashRequest> requests,
            int maxDegreeOfParallelism,
            IProgress<FormatHashProgress>? progress,
            CancellationToken cancellationToken) => throw new IOException("Synthetic fatal hash failure.");
    }
}
