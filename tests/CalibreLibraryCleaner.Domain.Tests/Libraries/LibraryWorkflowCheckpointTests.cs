using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class LibraryWorkflowCheckpointTests
{
    private static readonly DateTimeOffset ScannedAt = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly FormatFileFingerprint Fingerprint = new(10, new(new string('a', 64)));

    [Fact]
    public void ScanDefaultsConservativelyToRequiresExactAnalysis()
    {
        LibraryState state = LibraryState.FromScan(Snapshot(), Generation);

        state.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.RequiresExactAnalysis);
        state.WorkflowCheckpoint.GenerationId.Should().Be(Generation);
        state.WorkflowCheckpoint.Revision.Should().Be(state.Revision);
        state.WorkflowCheckpoint.PolicyVersions.Should().Be(LibraryWorkflowPolicyVersions.Current);
        state.WorkflowCheckpoint.PolicyVersions.ExactAnalysis.Should().Be("exact-analysis/1.1.0");
        state.WorkflowCheckpoint.PolicyVersions.CandidateAnalysis.Should().Be("candidate-analysis/1.1.0");
        state.IsWorkflowCheckpointCurrent.Should().BeTrue();
    }

    [Fact]
    public void DeltaPreservesPublishedCheckpointUntilWorkflowAdvances()
    {
        LibraryState exactReady = LibraryState.FromScan(Snapshot(), Generation)
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, ScannedAt);
        LibraryState projected = LibraryStateDeltaPolicy.Apply(exactReady,
            new RemoveFormatLibraryStateDelta(Generation, new(0), "remove-format:2:EPUB",
                ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint));

        projected.WorkflowCheckpoint.Should().Be(exactReady.WorkflowCheckpoint);
        projected.IsWorkflowCheckpointCurrent.Should().BeFalse();

        LibraryState prepared = projected.AdvanceWorkflow(
            LibraryWorkflowPhase.CandidatePreparationReady, ScannedAt.AddSeconds(2));
        prepared.WorkflowCheckpoint.Revision.Should().Be(projected.Revision);
        prepared.IsWorkflowCheckpointCurrent.Should().BeTrue();
    }

    [Fact]
    public void InvalidPhaseTransitionIsRejected()
    {
        LibraryState state = LibraryState.FromScan(Snapshot(), Generation);

        Action action = () => state.AdvanceWorkflow(
            LibraryWorkflowPhase.CandidatePreparationReady, ScannedAt);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UncertainStateNeverHasCurrentWorkflowAuthority()
    {
        LibraryState state = LibraryState.FromScan(Snapshot(), Generation)
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, ScannedAt)
            .MarkUncertain(new("AMBIGUOUS", "The mutation outcome is ambiguous.", ScannedAt.AddSeconds(1)));

        state.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.ExactReady);
        state.IsWorkflowCheckpointCurrent.Should().BeFalse();
        Action action = () => state.AdvanceWorkflow(
            LibraryWorkflowPhase.CandidatePreparationReady, ScannedAt.AddSeconds(2));
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CheckpointFromAnotherGenerationIsRejected()
    {
        LibrarySnapshot snapshot = Snapshot();
        LibraryWorkflowCheckpoint checkpoint = new(
            LibraryWorkflowPhase.ExactReady,
            new(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            new(0),
            LibraryWorkflowPolicyVersions.Current,
            ScannedAt);

        Action action = () => _ = new LibraryState(Generation, new(0), LibraryStateStatus.Authoritative,
            snapshot, ScannedAt, workflowCheckpoint: checkpoint);

        action.Should().Throw<ArgumentException>();
    }

    private static LibrarySnapshot Snapshot()
    {
        CalibreBook[] books = [Book(1), Book(2)];
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
            ScannedAt,
            books,
            [],
            ExactBinaryDuplicateDetector.Detect(books));
    }

    private static CalibreBook Book(long id)
    {
        string directory = $"Author/Book ({id})";
        return new(
            new(id), "Book", "Author", [new(new(id), "Author", "Author")], [],
            [new("EPUB", "book", $"{directory}/book.epub", FormatFileStatus.Present,
                Fingerprint, new(Fingerprint.SizeInBytes, ScannedAt, ScannedAt, 0))],
            directory);
    }
}
