using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class UnifiedCandidateCleanupPlannerTests
{
    [Fact]
    public void TransfersComplementaryFormatsAndRemovesEveryNonKeeperFormatAndRecord()
    {
        CalibreBook keeper = Book(1, Format("EPUB", 'a'));
        CalibreBook pdfSource = Book(2, Format("PDF", 'b'));
        CalibreBook mobiSource = Book(3, Format("MOBI", 'c'));
        LibraryState state = State([keeper, pdfSource, mobiSource]);
        UnifiedCandidateGroup group = state.Snapshot.UnifiedCandidateGroups.Single();
        List<ExecutionIssue> issues = [];

        UnifiedCandidateCleanupPlan plan = UnifiedCandidateCleanupPlanner.Build(
            state,
            state.GenerationId,
            state.Revision,
            [Selection(group, keeper.Id)],
            issues);

        issues.Should().BeEmpty();
        plan.Transfers.Select(value =>
            (value.SourceRecordId.Value, value.TargetRecordId.Value, value.SourceFormat.Format))
            .Should().Equal((3L, 1L, "MOBI"), (2L, 1L, "PDF"));
        plan.FormatRemovals.Select(value => (value.RecordId.Value, value.Format.Format))
            .Should().Equal((2L, "PDF"), (3L, "MOBI"));
        plan.RecordsToRemove.Select(value => value.Value).Should().Equal(2, 3);
    }

    [Fact]
    public void KeeperSameFormatWinsWithoutTransfer()
    {
        CalibreBook keeper = Book(1, Format("EPUB", 'a'));
        CalibreBook source = Book(2, Format("EPUB", 'b'));
        LibraryState state = State([keeper, source]);
        UnifiedCandidateGroup group = state.Snapshot.UnifiedCandidateGroups.Single();

        UnifiedCandidateCleanupPlan plan = UnifiedCandidateCleanupPlanner.Build(
            state, state.GenerationId, state.Revision, [Selection(group, keeper.Id)], []);

        plan.Transfers.Should().BeEmpty();
        plan.FormatRemovals.Should().ContainSingle(value =>
            value.RecordId == source.Id && value.Format.Fingerprint != keeper.Formats[0].Fingerprint);
        plan.RecordsToRemove.Should().Equal(source.Id);
    }

    [Fact]
    public void StaleMembershipAndIncompletePhysicalFactsAreSkippedBeforeMutation()
    {
        CalibreBook keeper = Book(1, Format("EPUB", 'a'));
        CalibreBook missing = Book(2, new BookFormat(
            "PDF", "book", "Author/missing.pdf", FormatFileStatus.Missing));
        LibraryState state = State([keeper, missing]);
        UnifiedCandidateGroup group = state.Snapshot.UnifiedCandidateGroups.Single();
        List<ExecutionIssue> issues = [];
        UnifiedCandidateCleanupSelection stale = new(
            group.Id, [keeper.Id], keeper.Id, Skip: false);
        UnifiedCandidateCleanupSelection incomplete = Selection(group, keeper.Id);

        UnifiedCandidateCleanupPlan stalePlan = UnifiedCandidateCleanupPlanner.Build(
            state, state.GenerationId, state.Revision, [stale], issues);
        UnifiedCandidateCleanupPlan incompletePlan = UnifiedCandidateCleanupPlanner.Build(
            state, state.GenerationId, state.Revision, [incomplete], issues);

        stalePlan.TotalOperations.Should().Be(0);
        incompletePlan.TotalOperations.Should().Be(0);
        issues.Should().Contain(value => value.Code == "CANDIDATE.GROUP_SELECTION_STALE");
        issues.Should().Contain(value => value.Code == "CANDIDATE.PHYSICAL_FACTS_INCOMPLETE");
    }

    [Fact]
    public void WrongGenerationOrRevisionIsRejected()
    {
        LibraryState state = State([Book(1, Format("EPUB", 'a')), Book(2, Format("EPUB", 'b'))]);
        UnifiedCandidateGroup group = state.Snapshot.UnifiedCandidateGroups.Single();

        Action generation = () => UnifiedCandidateCleanupPlanner.Build(
            state, new(Guid.NewGuid()), state.Revision, [Selection(group, group.GeneratedKeeperBookId)], []);
        Action revision = () => UnifiedCandidateCleanupPlanner.Build(
            state, state.GenerationId, state.Revision.Next(), [Selection(group, group.GeneratedKeeperBookId)], []);

        generation.Should().Throw<InvalidOperationException>();
        revision.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SkippedSelectionProducesDeterministicNothingToDoPlan()
    {
        LibraryState state = State([Book(1, Format("EPUB", 'a')), Book(2, Format("PDF", 'b'))]);
        UnifiedCandidateGroup group = state.Snapshot.UnifiedCandidateGroups.Single();
        UnifiedCandidateCleanupSelection selection = Selection(group, group.GeneratedKeeperBookId) with { Skip = true };

        UnifiedCandidateCleanupPlan plan = UnifiedCandidateCleanupPlanner.Build(
            state, state.GenerationId, state.Revision, [selection], []);

        plan.TotalOperations.Should().Be(0);
        plan.SkippedGroupCount.Should().Be(1);
    }

    [Fact]
    public void ToBeReviewedUnifiedGroupIsExecutableUnlessUserSkipsIt()
    {
        CalibreBook[] books = [Book(1, Format("EPUB", 'a')), Book(2, Format("EPUB", 'b'))];
        Domain.Duplicates.ExactMetadataDuplicateGroup metadata =
            Domain.Duplicates.ExactMetadataDuplicateDetector.Detect(books.Select(book => new CalibreBook(
                book.Id,
                "Shared title",
                book.AuthorSort,
                book.Authors,
                book.Identifiers,
                book.Formats,
                book.RelativeDirectory,
                book.PublicationMetadata))).Single();
        UnifiedCandidateGroup unified = UnifiedCandidateMergePolicy.Merge([metadata], [], books.Select(book => new CalibreBook(
            book.Id,
            "Shared title",
            book.AuthorSort,
            book.Authors,
            book.Identifiers,
            book.Formats,
            book.RelativeDirectory,
            book.PublicationMetadata))).Single();
        LibraryState state = StateWithGroup(books, unified);

        UnifiedCandidateCleanupPlan plan = UnifiedCandidateCleanupPlanner.Build(
            state, state.GenerationId, state.Revision, [Selection(unified, unified.GeneratedKeeperBookId)], []);

        unified.Classification.Should().Be(UnifiedCandidateClassification.ToBeReviewed);
        plan.TotalOperations.Should().Be(2);
    }

    private static UnifiedCandidateCleanupSelection Selection(
        UnifiedCandidateGroup group,
        CalibreBookId keeper) => new(group.Id, group.Members, keeper, Skip: false);

    private static LibraryState State(CalibreBook[] books)
    {
        WorkLanguageCandidateGroup expanded = WorkLanguageCandidateGroup.Create(
            "en",
            books.Select(value => value.Id),
            [books[0].Id],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
            contentComparison: new(books.Length - 1, books.Length - 1, 0, 0, 0, 0));
        UnifiedCandidateGroup unified = UnifiedCandidateMergePolicy.Merge([], [expanded], books).Single();
        return StateWithGroup(books, unified);
    }

    private static LibraryState StateWithGroup(CalibreBook[] books, UnifiedCandidateGroup unified)
    {
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
            DateTimeOffset.UnixEpoch,
            books,
            [],
            unifiedCandidateGroups: [unified]);
        LibraryStateGenerationId generation = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        LibraryWorkflowSource source = new(
            new(Guid.Parse("11111111-2222-3333-4444-555555555555")), new(3));
        return new(
            generation,
            new(0),
            LibraryStateStatus.Authoritative,
            snapshot,
            snapshot.ScannedAt,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.CandidateAnalysisReady,
                generation,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                snapshot.ScannedAt,
                source));
    }

    private static CalibreBook Book(long id, params BookFormat[] formats) => new(
        new(id),
        $"Book {id}",
        "Author",
        [new(new(id), "Author", "Author")],
        [],
        formats,
        $"Author/Book{id}",
        new(languages: ["eng"]));

    private static BookFormat Format(string format, char digest) => new(
        format,
        "book",
        $"Author/Book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        new(1_024, new(new string(digest, 64))),
        new(1_024, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));
}
