using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class ExpandedCandidateRetentionPolicyTests
{
    [Fact]
    public void HigherAssessedFormatQualityWinsBeforeMetadataCompleteness()
    {
        CalibreBook metadataRich = Book(1, hasCover: true, publisher: "Publisher");
        CalibreBook higherQuality = Book(2);
        WorkLanguageCandidateGroup group = Group(metadataRich.Id, higherQuality.Id);

        ExpandedCandidateRetentionDecision decision = ExpandedCandidateRetentionPolicy.Select(
            [group],
            [metadataRich, higherQuality],
            [Assessment(metadataRich.Id, 60), Assessment(higherQuality.Id, 95)]).Single();

        decision.KeeperBookId.Should().Be(higherQuality.Id);
        decision.Candidates.Should().BeInDescendingOrder(value => value.AssessmentScoreTotal);
    }

    [Fact]
    public void LowestRecordIdBreaksCompleteTieDeterministically()
    {
        CalibreBook first = Book(1);
        CalibreBook second = Book(2);

        ExpandedCandidateRetentionDecision decision = ExpandedCandidateRetentionPolicy.Select(
            [Group(second.Id, first.Id)], [second, first]).Single();

        decision.KeeperBookId.Should().Be(first.Id);
        decision.Candidates.Select(value => value.BookId.Value).Should().Equal(1, 2);
    }

    private static WorkLanguageCandidateGroup Group(params CalibreBookId[] ids) =>
        WorkLanguageCandidateGroup.Create(
            "en",
            ids,
            [ids[0]],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
            contentComparison: new(1, 1, 0, 0, 0, 0));

    private static CalibreBook Book(
        long id,
        bool hasCover = false,
        string? publisher = null) => new(
        new(id),
        $"Book {id}",
        "Author",
        [new(new(id), "Author", "Author")],
        [],
        [new(
            "EPUB",
            "Book",
            $"Author/Book{id}.epub",
            FormatFileStatus.Present,
            new(1_024, new(new string((char)('a' + id), 64))),
            new(1_024, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
        $"Author/Book{id}",
        new(publisher: publisher, languages: ["eng"], hasCover: hasCover));

    private static EpubAssessment Assessment(CalibreBookId bookId, int score)
    {
        AssessmentFinding finding = new(
            "EPUB.TEST.SCORE",
            FindingSeverity.Positive,
            score,
            "Synthetic score.");
        return new(
            bookId,
            "EPUB",
            $"Author/Book{bookId.Value}.epub",
            null,
            AssessmentStatus.Completed,
            new(score),
            new("epub-inspector/1.0.5"),
            new("epub-quality/1.0.3"),
            new(true, true),
            [finding]);
    }
}
