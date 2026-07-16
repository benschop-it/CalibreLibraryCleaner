using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Analysis;

public sealed class ExactMetadataDuplicateAnalysisTests
{
    [Fact]
    public async Task RealReadOnlyScanGroupsNormalizedMetadataIndependentlyOfBinaryContentAndAuthorOrder()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(
            1,
            "The Book : A Tale",
            ["Alice Smith", "Bob Jones"],
            ["Smith, Alice", "Jones, Bob"],
            [1, 2, 3],
            identifier: "9780000000001");
        library.AddMetadataBook(
            2,
            "the  book:a tale",
            ["Bob Jones", "Alice Smith"],
            ["Completely Different", "Sort Values"],
            [3, 2, 1],
            identifier: "9789999999999");
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactMetadataDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactMetadataDuplicateGroups[0].Members.Select(member => member.Value).Should().Equal(1, 2);
        outcome.Snapshot.ExactBinaryDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task MainAuthorOnlyRecordDoesNotMatchRecordWithAdditionalAuthors()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book", ["Main Author"], ["Author, Main"], [1]);
        library.AddMetadataBook(
            2,
            "Book",
            ["Main Author", "Second Author"],
            ["Author, Main", "Author, Second"],
            [2]);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.Snapshot!.ExactMetadataDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task IdenticalFilesWithDifferentMetadataRemainOnlyAnExactFileGroup()
    {
        using SyntheticCalibreLibrary library = new();
        byte[] content = [1, 2, 3];
        library.AddMetadataBook(1, "First Book", ["First Author"], ["First, Author"], content);
        library.AddMetadataBook(2, "Second Book", ["Second Author"], ["Second, Author"], content);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactMetadataDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task IdenticalFilesAndMetadataAppearInBothIndependentCollections()
    {
        using SyntheticCalibreLibrary library = new();
        byte[] content = [1, 2, 3];
        library.AddMetadataBook(1, "Book", ["Author"], ["Author"], content);
        library.AddMetadataBook(2, "BOOK", ["Author"], ["Different sort"], content);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactMetadataDuplicateGroups.Should().ContainSingle();
    }

    [Fact]
    public async Task RecordsWithoutAuthorsNeverGroupByTitleAlone()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book", [], [], [1]);
        library.AddMetadataBook(2, "Book", [], [], [2]);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactMetadataDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task SubtitleReversalAndInitialDifferencesRemainUngroupedThroughRealReader()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book: Subtitle", ["Jane Doe"], ["Doe, Jane"], [1]);
        library.AddMetadataBook(2, "Book", ["Jane Doe"], ["Doe, Jane"], [2]);
        library.AddMetadataBook(3, "Other", ["Doe, Jane"], ["Jane Doe"], [3]);
        library.AddMetadataBook(4, "Other", ["Jane Doe"], ["Doe, Jane"], [4]);
        library.AddMetadataBook(5, "Initials", ["J. R. R. Tolkien"], ["Tolkien, J. R. R."], [5]);
        library.AddMetadataBook(6, "Initials", ["JRR Tolkien"], ["Tolkien, JRR"], [6]);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ExactMetadataDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordWithValidAndBrokenAuthorLinksIsExcludedFromMetadataGrouping()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddMetadataBook(1, "Book", ["Jane Doe"], ["Doe, Jane"], [1]);
        library.AddBrokenAuthorLink(1, 999, 999);
        library.AddMetadataBook(2, "Book", ["Jane Doe"], ["Doe, Jane"], [2]);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(library.RootPath, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.Findings.Should().ContainSingle(finding =>
            finding.Code == "AUTHOR_REFERENCE_MISSING" &&
            finding.BookId.HasValue &&
            finding.BookId.Value.Value == 1);
        outcome.Snapshot.ExactMetadataDuplicateGroups.Should().BeEmpty();
    }
}
