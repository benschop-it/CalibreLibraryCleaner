using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteBulkExpandedCandidateCleanupUseCaseTests
{
    [Fact]
    public void PlanTransfersComplementaryFormatsThenRemovesNonKeepers()
    {
        CalibreBook keeper = Book(1, Format("EPUB", 'a'));
        CalibreBook pdfSource = Book(2, Format("PDF", 'b'));
        CalibreBook mobiSource = Book(3, Format("MOBI", 'c'));
        WorkLanguageCandidateGroup group = EligibleGroup(keeper.Id, pdfSource.Id, mobiSource.Id);
        LibrarySnapshot snapshot = Snapshot([keeper, pdfSource, mobiSource], [group]);
        List<ExecutionIssue> issues = [];

        ExecuteBulkExpandedCandidateCleanupUseCase.ExpandedCleanupPlan plan =
            ExecuteBulkExpandedCandidateCleanupUseCase.BuildPlan(
                snapshot,
                [new(group.Id, keeper.Id, Skip: false)],
                issues);

        issues.Should().BeEmpty();
        plan.Transfers.Select(value => (value.SourceRecordId.Value, value.TargetRecordId.Value, value.SourceFormat.Format))
            .Should().Equal((3L, 1L, "MOBI"), (2L, 1L, "PDF"));
        plan.FormatRemovals.Select(value => (value.RecordId.Value, value.Format.Format))
            .Should().Equal((2L, "PDF"), (3L, "MOBI"));
        plan.RecordsToRemove.Select(value => value.Value).Should().Equal(2, 3);
    }

    [Fact]
    public void ToBeReviewedGroupWithValidKeeperIsProcessed()
    {
        CalibreBook first = Book(1, Format("EPUB", 'a'));
        CalibreBook second = Book(2, Format("EPUB", 'b'));
        WorkLanguageCandidateGroup reviewOnly = WorkLanguageCandidateGroup.Create(
            "en",
            [first.Id, second.Id],
            [first.Id],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
            contentComparison: new(1, 1, 0, 0, 0, 0),
            policyVersion: MatchingPolicyVersion.V2);
        LibrarySnapshot snapshot = Snapshot([first, second], [reviewOnly]);
        List<ExecutionIssue> issues = [];

        ExecuteBulkExpandedCandidateCleanupUseCase.ExpandedCleanupPlan plan =
            ExecuteBulkExpandedCandidateCleanupUseCase.BuildPlan(
                snapshot,
                [new(reviewOnly.Id, first.Id, Skip: false)],
                issues);

        plan.TotalOperations.Should().Be(2);
        plan.SkippedGroupCount.Should().Be(0);
        issues.Should().BeEmpty();
    }

    [Fact]
    public void InvalidKeeperGroupIsSkipped()
    {
        CalibreBook first = Book(1, Format("EPUB", 'a'));
        CalibreBook second = Book(2, Format("EPUB", 'b'));
        WorkLanguageCandidateGroup group = EligibleGroup(first.Id, second.Id);
        List<ExecutionIssue> issues = [];

        ExecuteBulkExpandedCandidateCleanupUseCase.ExpandedCleanupPlan plan =
            ExecuteBulkExpandedCandidateCleanupUseCase.BuildPlan(
                Snapshot([first, second], [group]),
                [new(group.Id, new(99), Skip: false)],
                issues);

        plan.TotalOperations.Should().Be(0);
        plan.SkippedGroupCount.Should().Be(1);
        issues.Should().ContainSingle(value => value.Code == "BULK_EXPANDED.GROUP_SELECTION_INVALID");
    }

    [Fact]
    public void MissingPhysicalFactsSkipCompleteGroup()
    {
        CalibreBook first = Book(1, Format("EPUB", 'a'));
        CalibreBook second = Book(2, new(
            "EPUB", "Book", "Author/Missing.epub", FormatFileStatus.Missing));
        WorkLanguageCandidateGroup group = EligibleGroup(first.Id, second.Id);
        List<ExecutionIssue> issues = [];

        ExecuteBulkExpandedCandidateCleanupUseCase.ExpandedCleanupPlan plan =
            ExecuteBulkExpandedCandidateCleanupUseCase.BuildPlan(
                Snapshot([first, second], [group]),
                [new(group.Id, first.Id, Skip: false)],
                issues);

        plan.TotalOperations.Should().Be(0);
        plan.SkippedGroupCount.Should().Be(1);
        issues.Should().ContainSingle(value => value.Code == "BULK_EXPANDED.PHYSICAL_FACTS_INCOMPLETE");
    }

    private static WorkLanguageCandidateGroup EligibleGroup(params CalibreBookId[] ids) =>
        WorkLanguageCandidateGroup.Create(
            "en",
            ids,
            [ids[0]],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
            contentComparison: new(ids.Length - 1, ids.Length - 1, 0, 0, 0, 0));

    private static LibrarySnapshot Snapshot(
        CalibreBook[] books,
        WorkLanguageCandidateGroup[] groups) => new(
        new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
        DateTimeOffset.UnixEpoch,
        books,
        [],
        workLanguageCandidateGroups: groups,
        matchingRunSummary: new(
            MatchingPolicyVersion.Current,
            MatchingEvidenceStatus.Available,
            books.Length,
            groups.Sum(value => value.Members.Count - 1),
            groups.Sum(value => value.Members.Count - 1),
            0,
            books.Length,
            0,
            groups.Sum(value => value.ContentComparison.ComparedPairCount),
            groups.Length,
            0));

    private static CalibreBook Book(long id, BookFormat format) => new(
        new(id),
        $"Book {id}",
        "Author",
        [new(new(id), "Author", "Author")],
        [],
        [format],
        $"Author/Book{id}",
        new(languages: ["eng"]));

    private static BookFormat Format(string format, char digest) => new(
        format,
        "Book",
        $"Author/Book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        new(1_024, new(new string(digest, 64))),
        new(1_024, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));
}
