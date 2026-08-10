using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class LibraryStateSessionTests
{
    private static readonly DateTimeOffset ScannedAt = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly FormatFileFingerprint Fingerprint = new(10, new(new string('a', 64)));

    [Fact]
    public async Task SessionSerializesRevisionsForOneLibrary()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(0), "remove-format:2:EPUB",
            ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.Revision.Should().Be(new LibraryStateRevision(1));
        session.GetCurrent(snapshot.Identity.LibraryRoot).Should().BeSameAs(outcome.State);
    }

    [Fact]
    public async Task ExactAnalysisPublishesBaselineAlreadyAtExactReady()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");

        LibraryStateSessionOutcome outcome = await session.StartFromExactAnalysisAsync(
            snapshot, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.ExactReady);
        outcome.State.IsWorkflowCheckpointCurrent.Should().BeTrue();
        A.CallTo(() => store.WriteBaselineAsync(
            A<LibraryState>.That.Matches(state =>
                state.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.ExactReady),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => store.WriteWorkflowCheckpointAsync(
            A<string>._, A<LibraryState>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PostExactRefreshPublishesNewGenerationWithExactSourceProvenance()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState exact = (await session.StartFromExactAnalysisAsync(
            snapshot, CancellationToken.None)).State!;
        LibraryState completed = (await session.AdvanceWorkflowAsync(
            snapshot.Identity.LibraryRoot,
            LibraryWorkflowPhase.CandidatePreparationReady,
            snapshot.ScannedAt,
            CancellationToken.None)).State!;
        LibraryWorkflowSource source = new(completed.GenerationId, completed.Revision);
        Fake.ClearRecordedCalls(store);

        LibraryStateSessionOutcome outcome = await session.StartFromPostExactRefreshAsync(
            snapshot, source, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.GenerationId.Should().NotBe(exact.GenerationId);
        outcome.State.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidatePreparationReady);
        outcome.State.WorkflowCheckpoint.Source.Should().Be(source);
        outcome.State.IsWorkflowCheckpointCurrent.Should().BeTrue();
        session.GetCurrent(snapshot.Identity.LibraryRoot).Should().BeSameAs(outcome.State);
        A.CallTo(() => store.WritePostExactRefreshBaselineAsync(
            A<LibraryState>.That.Matches(value => value.WorkflowCheckpoint.Source == source),
            source,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PostExactRefreshRejectsSourceThatIsNoLongerCurrent()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState exact = (await session.StartFromExactAnalysisAsync(
            snapshot, CancellationToken.None)).State!;
        LibraryState completed = (await session.AdvanceWorkflowAsync(
            snapshot.Identity.LibraryRoot,
            LibraryWorkflowPhase.CandidatePreparationReady,
            snapshot.ScannedAt,
            CancellationToken.None)).State!;
        LibraryWorkflowSource staleSource = new(completed.GenerationId, completed.Revision);
        await session.StartFromExactAnalysisAsync(snapshot, CancellationToken.None);
        Fake.ClearRecordedCalls(store);

        LibraryStateSessionOutcome outcome = await session.StartFromPostExactRefreshAsync(
            snapshot, staleSource, CancellationToken.None);

        outcome.ErrorCode.Should().Be("LIBRARY_STATE.REFRESH_SOURCE_STALE");
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.GenerationId.Should().NotBe(exact.GenerationId);
        A.CallTo(() => store.WritePostExactRefreshBaselineAsync(
            A<LibraryState>._, A<LibraryWorkflowSource>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CandidateAnalysisPublishesAgainstCurrentRefreshedGeneration()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState exact = (await session.StartFromExactAnalysisAsync(
            snapshot, CancellationToken.None)).State!;
        LibraryState completed = (await session.AdvanceWorkflowAsync(
            snapshot.Identity.LibraryRoot,
            LibraryWorkflowPhase.CandidatePreparationReady,
            snapshot.ScannedAt,
            CancellationToken.None)).State!;
        LibraryState refreshed = (await session.StartFromPostExactRefreshAsync(
            snapshot,
            new(completed.GenerationId, completed.Revision),
            CancellationToken.None)).State!;
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(snapshot.Books).Single();
        UnifiedCandidateGroup unified = UnifiedCandidateMergePolicy.Merge(
            [metadata], [], snapshot.Books).Single();
        LibrarySnapshot analyzed = new(
            snapshot.Identity,
            snapshot.ScannedAt.AddSeconds(1),
            snapshot.Books,
            snapshot.Findings,
            snapshot.ExactBinaryDuplicateGroups,
            [metadata],
            unifiedCandidateGroups: [unified]);
        Fake.ClearRecordedCalls(store);

        LibraryStateSessionOutcome outcome = await session.CompleteCandidateAnalysisAsync(
            analyzed, refreshed.GenerationId, refreshed.Revision, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidateAnalysisReady);
        outcome.State.WorkflowCheckpoint.Source.Should().Be(refreshed.WorkflowCheckpoint.Source);
        outcome.State.Snapshot.UnifiedCandidateGroups.Should().ContainSingle();
        A.CallTo(() => store.WriteCandidateAnalysisAsync(
            analyzed.Identity.LibraryRoot,
            A<LibraryState>.That.Matches(value =>
                value.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.CandidateAnalysisReady),
            refreshed.WorkflowCheckpoint.Source!,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        exact.GenerationId.Should().NotBe(outcome.State.GenerationId);
    }

    [Fact]
    public async Task CandidateAnalysisRejectsStaleRefreshedGenerationBeforePersistence()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState exact = (await session.StartFromExactAnalysisAsync(
            snapshot, CancellationToken.None)).State!;
        LibraryState completed = (await session.AdvanceWorkflowAsync(
            snapshot.Identity.LibraryRoot,
            LibraryWorkflowPhase.CandidatePreparationReady,
            snapshot.ScannedAt,
            CancellationToken.None)).State!;
        LibraryState refreshed = (await session.StartFromPostExactRefreshAsync(
            snapshot,
            new(completed.GenerationId, completed.Revision),
            CancellationToken.None)).State!;
        await session.StartFromExactAnalysisAsync(snapshot, CancellationToken.None);
        Fake.ClearRecordedCalls(store);

        LibraryStateSessionOutcome outcome = await session.CompleteCandidateAnalysisAsync(
            snapshot, refreshed.GenerationId, refreshed.Revision, CancellationToken.None);

        outcome.ErrorCode.Should().Be("LIBRARY_STATE.CANDIDATE_SOURCE_STALE");
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.GenerationId.Should().NotBe(refreshed.GenerationId);
        A.CallTo(() => store.WriteCandidateAnalysisAsync(
            A<string>._,
            A<LibraryState>._,
            A<LibraryWorkflowSource>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        exact.GenerationId.Should().NotBe(refreshed.GenerationId);
    }

    [Fact]
    public async Task RejectedDeltaMarksStateUncertain()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(9), "remove-format:2:EPUB",
            ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.ErrorCode.Should().Be("LIBRARY_STATE.DELTA_REJECTED");
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
    }

    [Fact]
    public async Task UncertaintyPersistsAndBlocksLaterDelta()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;
        await session.MarkUncertainAsync(snapshot.Identity.LibraryRoot, new(
            "COMMAND_OUTCOME_AMBIGUOUS", "The command result was ambiguous.", ScannedAt.AddSeconds(1)),
            CancellationToken.None);

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(0), "remove-format:2:EPUB",
                ScannedAt.AddSeconds(2), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
    }

    [Fact]
    public async Task DifferentLibrariesKeepIndependentRevisions()
    {
        LibraryStateSession session = new();
        LibrarySnapshot first = Snapshot("C:\\first");
        LibrarySnapshot second = Snapshot("C:\\second");
        LibraryState firstState = (await session.StartFromScanAsync(first, CancellationToken.None)).State!;
        await session.StartFromScanAsync(second, CancellationToken.None);

        await session.ApplyAsync(first.Identity.LibraryRoot, new RemoveFormatLibraryStateDelta(
            firstState.GenerationId, new(0), "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint),
            CancellationToken.None);

        session.GetCurrent(first.Identity.LibraryRoot)!.Revision.Should().Be(new LibraryStateRevision(1));
        session.GetCurrent(second.Identity.LibraryRoot)!.Revision.Should().Be(new LibraryStateRevision(0));
    }

    [Fact]
    public async Task SuccessfulDeltaPublishesCommittedRevision()
    {
        using LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;
        List<LibraryState> published = [];
        session.StateChanged += (_, eventArgs) => published.Add(eventArgs.State);

        await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, baseline.Revision,
                "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint),
            CancellationToken.None);

        published.Should().ContainSingle().Which.Revision.Should().Be(new LibraryStateRevision(1));
    }

    [Fact]
    public async Task SuccessfulBatchPersistsAndPublishesOnlyFinalRevision()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;
        List<LibraryState> published = [];
        session.StateChanged += (_, eventArgs) => published.Add(eventArgs.State);
        LibraryStateDelta[] deltas =
        [
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, baseline.Revision,
                "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint),
            new RemoveRecordLibraryStateDelta(baseline.GenerationId, baseline.Revision.Next(),
                "remove-record:2", ScannedAt.AddSeconds(1), new(2)),
        ];

        LibraryStateSessionOutcome outcome = await session.ApplyBatchAsync(
            snapshot.Identity.LibraryRoot, deltas, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.Revision.Should().Be(new LibraryStateRevision(2));
        published.Should().ContainSingle().Which.Should().BeSameAs(outcome.State);
        A.CallTo(() => store.AppendDeltaBatchAsync(snapshot.Identity.LibraryRoot,
            A<IReadOnlyList<LibraryStateDelta>>.That.Matches(value => value.Count == 2),
            outcome.State, false, null, false,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => store.AppendDeltaAsync(A<string>._, A<LibraryStateDelta>._,
            A<LibraryState>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MutationBatchPersistsIntentBeforeCompletedDeltaBatch()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;
        LibraryStateDelta[] deltas =
        [
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, baseline.Revision,
                "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint),
            new RemoveRecordLibraryStateDelta(baseline.GenerationId, baseline.Revision.Next(),
                "remove-record:2", ScannedAt.AddSeconds(1), new(2)),
        ];
        LibraryStateMutationIntent intent = new("chunk-1", baseline.GenerationId, baseline.Revision,
            deltas.Length, ScannedAt.AddSeconds(1));

        LibraryStateSessionOutcome began = await session.BeginMutationBatchAsync(
            snapshot.Identity.LibraryRoot, intent, CancellationToken.None);
        LibraryStateSessionOutcome applied = await session.ApplyMutationBatchAsync(
            snapshot.Identity.LibraryRoot, intent.IntentId, deltas,
            completeMutationIntent: true, CancellationToken.None);

        began.IsSuccess.Should().BeTrue();
        applied.IsSuccess.Should().BeTrue();
        A.CallTo(() => store.WriteMutationIntentAsync(snapshot.Identity.LibraryRoot, intent,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly()
            .Then(A.CallTo(() => store.AppendDeltaBatchAsync(snapshot.Identity.LibraryRoot,
                A<IReadOnlyList<LibraryStateDelta>>._, applied.State!, false,
                intent.IntentId, true, A<CancellationToken>._)).MustHaveHappenedOnceExactly());
    }

    [Fact]
    public async Task WorkflowAdvancePersistsBeforePublishingCurrentCheckpoint()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        await session.StartFromScanAsync(snapshot, CancellationToken.None);
        List<LibraryState> published = [];
        session.StateChanged += (_, eventArgs) => published.Add(eventArgs.State);

        LibraryStateSessionOutcome outcome = await session.AdvanceWorkflowAsync(
            snapshot.Identity.LibraryRoot,
            LibraryWorkflowPhase.ExactReady,
            ScannedAt,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.IsWorkflowCheckpointCurrent.Should().BeTrue();
        published.Should().ContainSingle().Which.Should().BeSameAs(outcome.State);
        A.CallTo(() => store.WriteWorkflowCheckpointAsync(
            snapshot.Identity.LibraryRoot, outcome.State, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WorkflowPersistenceFailureLeavesEarlierPhaseCurrent()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        A.CallTo(() => store.WriteWorkflowCheckpointAsync(
                A<string>._, A<LibraryState>._, A<CancellationToken>._))
            .ThrowsAsync(new IOException("controlled persistence failure"));
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;

        LibraryStateSessionOutcome outcome = await session.AdvanceWorkflowAsync(
            snapshot.Identity.LibraryRoot,
            LibraryWorkflowPhase.ExactReady,
            ScannedAt,
            CancellationToken.None);

        outcome.ErrorCode.Should().Be("LIBRARY_STATE.WORKFLOW_CHECKPOINT_FAILED");
        session.GetCurrent(snapshot.Identity.LibraryRoot).Should().BeSameAs(baseline);
    }

    [Fact]
    public async Task UncertaintyPersistenceFailureIsReportedAndRemainsBlockedInMemory()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        A.CallTo(() => store.WriteUncertaintyAsync(A<string>._, A<LibraryState>._,
                A<CancellationToken>._))
            .ThrowsAsync(new IOException("controlled persistence failure"));
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(9), "stale-delta",
                ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.ErrorCode.Should().Be("LIBRARY_STATE.UNCERTAINTY_PERSIST_FAILED");
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
    }

    private static LibrarySnapshot Snapshot(string root)
    {
        CalibreBook[] books = [Book(1), Book(2)];
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, root),
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
