using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class UnifiedCandidateMergePolicyTests
{
    [Fact]
    public void MetadataOnlyGroupSurvivesWithoutContentEvidence()
    {
        CalibreBook[] books = [Book(1), Book(2)];
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();

        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [metadata], [], books).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
        group.Classification.Should().Be(UnifiedCandidateClassification.ToBeReviewed);
        group.EvidenceSources.Should().HaveFlag(UnifiedCandidateEvidenceSource.ExactMetadata);
        group.EvidenceSources.Should().HaveFlag(UnifiedCandidateEvidenceSource.Title);
        group.EvidenceSources.Should().HaveFlag(UnifiedCandidateEvidenceSource.Author);
        group.ExpandedGroupIds.Should().BeEmpty();
    }

    [Fact]
    public void ExactMetadataCandidatesBypassOrdinaryTwentyCandidateCap()
    {
        CalibreBook[] books = Enumerable.Range(1, 25).Select(id => Book(id)).ToArray();
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();

        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [metadata], [], books).Single();

        group.Members.Should().HaveCount(25);
        group.Members.Select(value => value.Value).Should().Equal(Enumerable.Range(1, 25).Select(value => (long)value));
    }

    [Fact]
    public void CompatibleMetadataOverlapAttachesUnassignedMemberAndCombinesProvenance()
    {
        CalibreBook[] books = [Book(1), Book(2), Book(3)];
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();
        WorkLanguageCandidateGroup expanded = Expanded("en", books[0].Id, books[1].Id);

        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [metadata], [expanded], books).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2, 3);
        group.ExactMetadataGroupIds.Should().ContainSingle().Which.Should().Be(metadata.Id);
        group.ExpandedGroupIds.Should().ContainSingle().Which.Should().Be(expanded.Id);
        group.EvidenceSources.Should().HaveFlag(UnifiedCandidateEvidenceSource.Content);
        group.Classification.Should().Be(UnifiedCandidateClassification.ToBeReviewed);
    }

    [Fact]
    public void KnownLanguageContradictionPreservesExpandedComponentsAndDisjointness()
    {
        CalibreBook[] books =
        [
            Book(1, "eng"), Book(2, "eng"), Book(3, "nld"), Book(4, "nld"),
        ];
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();
        WorkLanguageCandidateGroup english = Expanded("en", books[0].Id, books[1].Id);
        WorkLanguageCandidateGroup dutch = Expanded("nl", books[2].Id, books[3].Id);

        IReadOnlyList<UnifiedCandidateGroup> groups = UnifiedCandidateMergePolicy.Merge(
            [metadata], [english, dutch], books);

        groups.Should().HaveCount(2);
        groups.SelectMany(value => value.Members).Should().OnlyHaveUniqueItems();
        groups.Should().OnlyContain(value => value.Classification == UnifiedCandidateClassification.ToBeReviewed);
        groups.Should().OnlyContain(value => value.ReviewFindings.Any(finding =>
            finding.Code == "UNIFIED.METADATA_OVERLAP_CONTRADICTED"));
    }

    [Fact]
    public void ConflictingStrongIdentifiersPreventMetadataBridgeMerge()
    {
        CalibreBook[] books =
        [
            Book(1, identifier: "9780306406157"), Book(2, identifier: "9780306406157"),
            Book(3, identifier: "9780140328721"), Book(4, identifier: "9780140328721"),
        ];
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();

        IReadOnlyList<UnifiedCandidateGroup> groups = UnifiedCandidateMergePolicy.Merge(
            [metadata], [Expanded("en", books[0].Id, books[1].Id), Expanded("en", books[2].Id, books[3].Id)], books);

        groups.Should().HaveCount(2);
        groups.SelectMany(value => value.Members).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void IncompatibleSingletonMetadataMemberProducesVisibleReviewFinding()
    {
        CalibreBook english = Book(1, identifier: "9780306406157");
        CalibreBook expandedPeer = Book(2, identifier: "9780306406157");
        CalibreBook contradicted = Book(3, identifier: "9780140328721");
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect([english, contradicted]).Single();
        WorkLanguageCandidateGroup expanded = Expanded("en", english.Id, expandedPeer.Id);

        CandidateMetadataNormalizer.NormalizeStrongIdentifier("isbn", "9780306406157")
            .Should().NotBeNull();
        CandidateMetadataNormalizer.NormalizeStrongIdentifier("isbn", "9780140328721")
            .Should().NotBeNull().And.NotBe(
                CandidateMetadataNormalizer.NormalizeStrongIdentifier("isbn", "9780306406157"));

        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [metadata], [expanded], [english, expandedPeer, contradicted]).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
        group.ReviewFindings.Should().ContainSingle(value =>
            value.Code == "UNIFIED.METADATA_MEMBER_CONTRADICTED"
            && value.RelatedBookIds.Contains(contradicted.Id));
        group.Classification.Should().Be(UnifiedCandidateClassification.ToBeReviewed);
    }

    [Fact]
    public void SeriesIndexContradictionPreventsCrossComponentMerge()
    {
        CalibreBook[] books =
        [
            Book(1, series: "Series", seriesIndex: 1), Book(2, series: "Series", seriesIndex: 1),
            Book(3, series: "Series", seriesIndex: 2), Book(4, series: "Series", seriesIndex: 2),
        ];
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();

        IReadOnlyList<UnifiedCandidateGroup> groups = UnifiedCandidateMergePolicy.Merge(
            [metadata], [Expanded("en", books[0].Id, books[1].Id), Expanded("en", books[2].Id, books[3].Id)], books);

        groups.Should().HaveCount(2);
        groups.SelectMany(value => value.Members).Should().OnlyHaveUniqueItems();
        groups.Should().OnlyContain(value => value.ReviewFindings.Count > 0);
    }

    [Fact]
    public void EditionMarkerContradictionPreventsMetadataAttachment()
    {
        CalibreBook plain = Book(1, title: "Shared Book");
        CalibreBook abridged = Book(2, title: "Shared Book Abridged");
        CalibreBook metadataPeer = Book(3, title: "Shared Book");
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect([plain, metadataPeer]).Single();

        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [metadata], [Expanded("en", plain.Id, abridged.Id)], [plain, abridged, metadataPeer]).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
        group.ReviewFindings.Should().Contain(value => value.Code == "UNIFIED.METADATA_MEMBER_CONTRADICTED");
    }

    [Fact]
    public void DifferentContentSummaryPreventsCrossComponentMerge()
    {
        CalibreBook[] books = [Book(1), Book(2), Book(3), Book(4)];
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();
        WorkLanguageCandidateGroup different = WorkLanguageCandidateGroup.Create(
            "en",
            [books[0].Id, books[1].Id],
            [books[0].Id],
            WorkLanguageCandidateConfidence.Ambiguous,
            [new("MATCH.CONTENT.DIFFERENT", CandidateEvidenceStrength.Weak)],
            [new("MATCH.CONTENT.DIFFERENT")],
            new(1, 0, 0, 0, 1, 0));

        IReadOnlyList<UnifiedCandidateGroup> groups = UnifiedCandidateMergePolicy.Merge(
            [metadata], [different, Expanded("en", books[2].Id, books[3].Id)], books);

        groups.Should().HaveCount(2);
        groups.SelectMany(value => value.Members).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AssessmentQualitySelectsGeneratedKeeperBeforeMetadataCompleteness()
    {
        CalibreBook[] books = [Book(1, hasCover: true), Book(2)];
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();

        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [metadata], [], books, [Assessment(books[0].Id, 60), Assessment(books[1].Id, 95)]).Single();

        group.GeneratedKeeperBookId.Should().Be(books[1].Id);
    }

    [Fact]
    public void SnapshotRejectsRecordMembershipInMoreThanOneUnifiedGroup()
    {
        CalibreBook[] books = [Book(1), Book(2), Book(3)];
        UnifiedCandidateGroup first = MetadataGroup(books, 1, 2);
        UnifiedCandidateGroup second = MetadataGroup(books, 2, 3);

        Action action = () => _ = new LibrarySnapshot(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
            DateTimeOffset.UnixEpoch,
            books,
            [],
            unifiedCandidateGroups: [first, second]);

        action.Should().Throw<ArgumentException>();
    }

    private static UnifiedCandidateGroup MetadataGroup(CalibreBook[] books, params long[] ids)
    {
        CalibreBook[] selected = ids.Select(id => books.Single(value => value.Id.Value == id)).ToArray();
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(selected).Single();
        return UnifiedCandidateMergePolicy.Merge([metadata], [], selected).Single();
    }

    private static WorkLanguageCandidateGroup Expanded(string language, params CalibreBookId[] members) =>
        WorkLanguageCandidateGroup.Create(
            language,
            members,
            [members[0]],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
            contentComparison: new(1, 1, 0, 0, 0, 0));

    private static CalibreBook Book(
        int id,
        string language = "eng",
        string? identifier = null,
        bool hasCover = false,
        string title = "Shared Book",
        string? series = null,
        decimal? seriesIndex = null) => new(
        new(id),
        title,
        "Author",
        [new(new(id), "Author", "Author")],
        identifier is null ? [] : [new("isbn", identifier)],
        [new(
            "EPUB",
            "book",
            $"Author/Book {id}/book.epub",
            FormatFileStatus.Present,
            new(100 + id, new(new string('a', 64))),
            new(100 + id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
        $"Author/Book {id}",
        new(
            publisher: hasCover ? "Publisher" : null,
            series: series,
            seriesIndex: seriesIndex,
            languages: [language],
            hasCover: hasCover));

    private static EpubAssessment Assessment(CalibreBookId bookId, int score) => new(
        bookId,
        "EPUB",
        $"Author/Book {bookId.Value}/book.epub",
        null,
        AssessmentStatus.Completed,
        new(score),
        new("epub-inspector/1.0.5"),
        new("epub-quality/1.0.3"),
        new(true, true),
        [new("EPUB.TEST", FindingSeverity.Positive, score, "Synthetic score.")]);
}
