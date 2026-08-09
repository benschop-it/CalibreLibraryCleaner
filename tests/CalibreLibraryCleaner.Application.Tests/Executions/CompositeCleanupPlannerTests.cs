using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class CompositeCleanupPlannerTests
{
    [Fact]
    public void CompatibleOverlapAcrossAllCategoriesDeduplicatesOperations()
    {
        Fixture fixture = Fixture.Create();

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [new(fixture.ExactGroup.Id, fixture.ExactGroup.Members.Single(value => value.BookId == fixture.First.Id))],
            [new(fixture.MetadataGroup.Id, fixture.First.Id, Skip: false)],
            [new(fixture.ExpandedGroup.Id, fixture.First.Id, Skip: false)]);

        plan.Conflicts.Should().BeEmpty();
        plan.Transfers.Should().BeEmpty();
        plan.FormatRemovals.Should().ContainSingle(value =>
            value.RecordId == fixture.Second.Id && value.Format.Format == "EPUB");
        plan.RecordsToRemove.Should().Equal(fixture.Second.Id);
        plan.Summary.TotalOperationCount.Should().Be(2);
    }

    [Fact]
    public void DifferentGeneratedKeepersForOverlappingGroupsReconcileGlobally()
    {
        Fixture fixture = Fixture.Create();

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [],
            [new(fixture.MetadataGroup.Id, fixture.First.Id, Skip: false)],
            [new(fixture.ExpandedGroup.Id, fixture.Second.Id, Skip: false)]);

        plan.Conflicts.Should().BeEmpty();
        plan.Summary.ReconciledKeeperCount.Should().Be(1);
        plan.FormatRemovals.Should().ContainSingle(value => value.RecordId == fixture.Second.Id);
        plan.RecordsToRemove.Should().Equal(fixture.Second.Id);
    }

    [Fact]
    public void DifferentExplicitKeepersForOverlappingGroupsBlock()
    {
        Fixture fixture = Fixture.Create();

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [],
            [new(fixture.MetadataGroup.Id, fixture.First.Id, Skip: false, KeeperWasOverridden: true)],
            [new(fixture.ExpandedGroup.Id, fixture.Second.Id, Skip: false, KeeperWasOverridden: true)]);

        plan.Conflicts.Should().ContainSingle(value =>
            value.Code == "COMPOSITE.EXPLICIT_KEEPER_CONFLICT");
    }

    [Fact]
    public void TransitiveOverlapSelectsBestKeeperAcrossCompleteComponent()
    {
        FormatFileFingerprint epub = new(1_024, new(new string('a', 64)));
        FormatFileFingerprint pdf = new(2_048, new(new string('b', 64)));
        CalibreBook first = Fixture.Book(1, epub);
        CalibreBook second = Fixture.Book(2, epub);
        CalibreBook thirdBase = Fixture.Book(3, epub);
        CalibreBook third = new(
            thirdBase.Id,
            thirdBase.Title,
            thirdBase.AuthorSort,
            thirdBase.Authors,
            thirdBase.Identifiers,
            [
                .. thirdBase.Formats,
                new("PDF", "book", "Author/Shared Book (3)/book.pdf", FormatFileStatus.Present,
                    pdf, new(pdf.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0)),
            ],
            thirdBase.RelativeDirectory,
            thirdBase.PublicationMetadata);
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect([first, second]).Single();
        WorkLanguageCandidateGroup expanded = ExpandedGroup(second.Id, third.Id);
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
            DateTimeOffset.UnixEpoch,
            [first, second, third], [], exactMetadataDuplicateGroups: [metadata],
            workLanguageCandidateGroups: [expanded],
            matchingRunSummary: new(MatchingPolicyVersion.Current,
                MatchingEvidenceStatus.Available, 3, 2, 2, 0, 3, 0, 2, 1, 0));

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            snapshot,
            [],
            [new(metadata.Id, first.Id, Skip: false)],
            [new(expanded.Id, second.Id, Skip: false)]);

        plan.Conflicts.Should().BeEmpty();
        plan.Summary.ReconciledKeeperCount.Should().Be(2);
        plan.RecordsToRemove.Should().Equal(first.Id, second.Id);
        plan.FormatRemovals.Should().OnlyContain(value =>
            value.RecordId == first.Id || value.RecordId == second.Id);
    }

    [Fact]
    public void GeneratedExactKeeperIsReconciledWithGlobalRecordKeeper()
    {
        Fixture fixture = Fixture.Create();

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [new(fixture.ExactGroup.Id, fixture.ExactGroup.Members.Single(value => value.BookId == fixture.Second.Id))],
            [new(fixture.MetadataGroup.Id, fixture.First.Id, Skip: false)],
            []);

        plan.Conflicts.Should().BeEmpty();
        plan.FormatRemovals.Should().ContainSingle(value => value.RecordId == fixture.Second.Id);
        plan.RecordsToRemove.Should().Equal(fixture.Second.Id);
    }

    [Fact]
    public void ToBeReviewedExpandedSelectionIsIncludedInCompositeCleanup()
    {
        Fixture fixture = Fixture.Create(expandedPolicyVersion: MatchingPolicyVersion.V2);

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [new(fixture.ExactGroup.Id, fixture.ExactGroup.Members[0], Skip: true)],
            [],
            [new(fixture.ExpandedGroup.Id, fixture.First.Id, Skip: false)]);

        plan.Conflicts.Should().BeEmpty();
        plan.Summary.ExpandedSelectionCount.Should().Be(1);
        plan.Summary.TotalOperationCount.Should().Be(2);
    }

    [Fact]
    public void IncompleteExpandedSelectionIsSkippedWithoutBlockingOtherCategories()
    {
        FormatFileFingerprint fingerprint = new(1_024, new(new string('a', 64)));
        CalibreBook first = Fixture.Book(1, fingerprint);
        CalibreBook second = Fixture.Book(2, fingerprint, FormatFileStatus.Missing);
        WorkLanguageCandidateGroup expanded = WorkLanguageCandidateGroup.Create(
            "en", [first.Id, second.Id], [first.Id], WorkLanguageCandidateConfidence.Probable,
            [new("MATCH.CONTENT.AMBIGUOUS", CandidateEvidenceStrength.Supporting)],
            contentComparison: new(1, 0, 0, 1, 0, 0));
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
            DateTimeOffset.UnixEpoch,
            [first, second], [], workLanguageCandidateGroups: [expanded],
            matchingRunSummary: new(MatchingPolicyVersion.Current,
                MatchingEvidenceStatus.Available, 2, 1, 1, 0, 2, 0, 1, 1, 0));

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            snapshot,
            [],
            [],
            [new(expanded.Id, first.Id, Skip: false)]);

        plan.Conflicts.Should().BeEmpty();
        plan.Summary.ExpandedSelectionCount.Should().Be(0);
        plan.Summary.SkippedSelectionCount.Should().Be(1);
        plan.Summary.TotalOperationCount.Should().Be(0);
    }

    [Fact]
    public void ExplicitlySkippedExactGroupDoesNotBecomeMissingSelectionConflict()
    {
        Fixture fixture = Fixture.Create();
        ExactBinaryDuplicateMember retained = fixture.ExactGroup.Members[0];

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [new(fixture.ExactGroup.Id, retained, Skip: true)],
            [],
            []);

        plan.Conflicts.Should().BeEmpty();
        plan.Summary.TotalOperationCount.Should().Be(0);
        plan.Summary.ExactSelectionCount.Should().Be(0);
    }

    [Fact]
    public void ExactOnlySelectionRetainsGeneratedKeeperBehavior()
    {
        Fixture fixture = Fixture.Create();
        ExactBinaryDuplicateMember retained = fixture.ExactGroup.Members.Single(value =>
            value.BookId == fixture.First.Id);

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [new(fixture.ExactGroup.Id, retained)],
            [],
            []);

        plan.Conflicts.Should().BeEmpty();
        plan.Summary.ExactSelectionCount.Should().Be(1);
        plan.FormatRemovals.Should().ContainSingle(value => value.RecordId == fixture.Second.Id);
        plan.RecordsToRemove.Should().Equal(fixture.Second.Id);
    }

    private sealed record Fixture(
        LibrarySnapshot Snapshot,
        CalibreBook First,
        CalibreBook Second,
        ExactBinaryDuplicateGroup ExactGroup,
        ExactMetadataDuplicateGroup MetadataGroup,
        WorkLanguageCandidateGroup ExpandedGroup)
    {
        public static Fixture Create(MatchingPolicyVersion? expandedPolicyVersion = null)
        {
            FormatFileFingerprint fingerprint = new(1_024, new(new string('a', 64)));
            CalibreBook first = Book(1, fingerprint);
            CalibreBook second = Book(2, fingerprint);
            ExactBinaryDuplicateGroup exact = ExactBinaryDuplicateDetector.Detect([first, second]).Single();
            ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect([first, second]).Single();
            WorkLanguageCandidateGroup expanded = WorkLanguageCandidateGroup.Create(
                "en",
                [first.Id, second.Id],
                [first.Id, second.Id],
                WorkLanguageCandidateConfidence.Strong,
                [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
                contentComparison: new(1, 1, 0, 0, 0, 0),
                policyVersion: expandedPolicyVersion ?? MatchingPolicyVersion.Current);
            LibrarySnapshot snapshot = new(
                new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
                DateTimeOffset.UnixEpoch,
                [first, second],
                [],
                [exact],
                [metadata],
                workLanguageCandidateGroups: [expanded],
                matchingRunSummary: new(
                    expandedPolicyVersion ?? MatchingPolicyVersion.Current,
                    MatchingEvidenceStatus.Available,
                    2,
                    1,
                    1,
                    0,
                    2,
                    0,
                    1,
                    1,
                    0));
            return new(snapshot, first, second, exact, metadata, expanded);
        }

        public static CalibreBook Book(
            long id,
            FormatFileFingerprint fingerprint,
            FormatFileStatus status = FormatFileStatus.Present) => new(
            new(id),
            "Shared Book",
            "Author",
            [new(new(id), "Author", "Author")],
            [],
            [new(
                "EPUB",
                "book",
                $"Author/Shared Book ({id})/book.epub",
                status,
                status == FormatFileStatus.Present ? fingerprint : null,
                status == FormatFileStatus.Present
                    ? new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0)
                    : null)],
            $"Author/Shared Book ({id})",
            new(languages: ["eng"]));
    }

    private static WorkLanguageCandidateGroup ExpandedGroup(
        CalibreBookId first,
        CalibreBookId second) => WorkLanguageCandidateGroup.Create(
        "en", [first, second], [first], WorkLanguageCandidateConfidence.Strong,
        [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
        contentComparison: new(1, 1, 0, 0, 0, 0));
}
