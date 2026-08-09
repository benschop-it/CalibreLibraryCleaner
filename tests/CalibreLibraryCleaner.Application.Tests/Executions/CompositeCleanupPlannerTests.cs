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
    public void DifferentKeepersForOverlappingRecordGroupsBlock()
    {
        Fixture fixture = Fixture.Create();

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [],
            [new(fixture.MetadataGroup.Id, fixture.First.Id, Skip: false)],
            [new(fixture.ExpandedGroup.Id, fixture.Second.Id, Skip: false)]);

        plan.Conflicts.Should().Contain(value =>
            value.Code == "COMPOSITE.RECORD_MULTIPLE_KEEPERS"
            || value.Code == "COMPOSITE.KEEPER_REMOVED");
    }

    [Fact]
    public void ExactRetainedRecordRemovedByMetadataBlocks()
    {
        Fixture fixture = Fixture.Create();

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [new(fixture.ExactGroup.Id, fixture.ExactGroup.Members.Single(value => value.BookId == fixture.Second.Id))],
            [new(fixture.MetadataGroup.Id, fixture.First.Id, Skip: false)],
            []);

        plan.Conflicts.Should().Contain(value =>
            value.Code == "COMPOSITE.KEEPER_REMOVED"
            || value.Code == "COMPOSITE.RETAINED_FORMAT_REMOVED");
    }

    [Fact]
    public void ReviewOnlyExpandedSelectionBecomesBlockingConflict()
    {
        Fixture fixture = Fixture.Create(expandedPolicyVersion: MatchingPolicyVersion.V2);

        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            fixture.Snapshot,
            [],
            [],
            [new(fixture.ExpandedGroup.Id, fixture.First.Id, Skip: false)]);

        plan.Conflicts.Should().ContainSingle(value =>
            value.Category == CompositeCleanupCategory.Expanded
            && value.Code.Contains("GROUP_SELECTION_INVALID", StringComparison.Ordinal));
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

        private static CalibreBook Book(long id, FormatFileFingerprint fingerprint) => new(
            new(id),
            "Shared Book",
            "Author",
            [new(new(id), "Author", "Author")],
            [],
            [new(
                "EPUB",
                "book",
                $"Author/Shared Book ({id})/book.epub",
                FormatFileStatus.Present,
                fingerprint,
                new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
            $"Author/Shared Book ({id})",
            new(languages: ["eng"]));
    }
}
