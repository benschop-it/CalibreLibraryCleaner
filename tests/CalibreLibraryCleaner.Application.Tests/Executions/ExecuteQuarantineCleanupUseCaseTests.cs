using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteQuarantineCleanupUseCaseTests
{
    [Fact]
    public async Task CopiesAllBeforeDeletingAndProjectsVerifiedRemoval()
    {
        Harness harness = new(copyFails: false);

        QuarantineCleanupResult result = await harness.ExecuteAsync();

        result.IsCompleted.Should().BeTrue();
        result.CopiedRecordCount.Should().Be(1);
        result.RemovedSourceRecordCount.Should().Be(1);
        harness.Chunks.SelectMany(value => value.Operations).Select(value => value.Kind).Should().Equal(
            CalibreMutationOperationKind.CopyRecordToQuarantine,
            CalibreMutationOperationKind.RemoveRecordWithContent);
        harness.AppliedDeltas.Should().ContainSingle()
            .Which.Should().BeOfType<RemoveRecordWithContentLibraryStateDelta>();
    }

    [Fact]
    public async Task FailedCopyStopsBeforeDeleteAndMarksSourceUncertain()
    {
        Harness harness = new(copyFails: true);

        QuarantineCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(QuarantineCleanupState.PartiallyCompleted);
        harness.Chunks.SelectMany(value => value.Operations).Should().OnlyContain(value =>
            value.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine);
        harness.AppliedDeltas.Should().BeEmpty();
        A.CallTo(() => harness.State.MarkUncertainAsync(
                harness.LibraryRoot,
                A<LibraryStateUncertainty>.That.Matches(value => value.Code == "QUARANTINE.COPY_FAILED"),
                CancellationToken.None))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task MultipleCopyChunksFinishBeforeAnySingleRecordDeletionInExistingDestination()
    {
        Harness harness = new(copyFails: false, recordCount: 101, destinationExists: true);

        QuarantineCleanupResult result = await harness.ExecuteAsync();

        result.IsCompleted.Should().BeTrue();
        result.CopiedRecordCount.Should().Be(101);
        result.RemovedSourceRecordCount.Should().Be(101);
        harness.Chunks.Take(2).SelectMany(value => value.Operations).Should().HaveCount(101)
            .And.OnlyContain(value => value.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine);
        harness.Chunks.Skip(2).Should().HaveCount(101)
            .And.OnlyContain(value => value.Operations.Count == 1
                && value.Operations[0].Kind == CalibreMutationOperationKind.RemoveRecordWithContent);
    }

    [Fact]
    public async Task FailureInSecondCopyChunkNeverStartsSourceDeletion()
    {
        Harness harness = new(copyFails: true, recordCount: 101, copyFailureRecord: 101);

        QuarantineCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(QuarantineCleanupState.PartiallyCompleted);
        result.CopiedRecordCount.Should().Be(100);
        result.RemovedSourceRecordCount.Should().Be(0);
        harness.Chunks.SelectMany(value => value.Operations).Should().OnlyContain(value =>
            value.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine);
        harness.AppliedDeltas.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedDeletionStopsWithoutRetryingOrDeletingTheNextSource()
    {
        Harness harness = new(copyFails: false, recordCount: 2, deleteFails: true);

        QuarantineCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(QuarantineCleanupState.PartiallyCompleted);
        result.CopiedRecordCount.Should().Be(2);
        harness.Chunks.Should().HaveCount(2);
        harness.Chunks[1].Operations.Should().ContainSingle();
        harness.AppliedDeltas.Should().BeEmpty();
    }

    private sealed class Harness
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        private readonly ExecuteQuarantineCleanupUseCase _useCase;
        private readonly ExecuteQuarantineCleanupRequest _request;

        public Harness(
            bool copyFails,
            int recordCount = 1,
            int copyFailureRecord = 1,
            bool deleteFails = false,
            bool destinationExists = false)
        {
            LibraryRoot = "C:\\Libraries\\Source";
            FormatFileFingerprint fingerprint = new(12, new(new string('a', 64)));
            BookFormat format = new(
                "EPUB",
                "book.epub",
                "Author/Book (1)/book.epub",
                FormatFileStatus.Present,
                fingerprint,
                new(12, Now, Now, 0));
            CalibreBook book = new(
                new(1),
                "Unknown",
                "Author, Reliable",
                [new(new(1), "Reliable Author", "Author, Reliable")],
                [],
                [format],
                "Author/Book (1)");
            CalibreBook[] books = Enumerable.Range(1, recordCount).Select(id => id == 1 ? book : new CalibreBook(
                new(id), book.Title, book.AuthorSort, book.Authors, book.Identifiers,
                [new("EPUB", "book.epub", $"Author/Book ({id})/book.epub", FormatFileStatus.Present,
                    fingerprint, format.Observation)], $"Author/Book ({id})")).ToArray();
            LibraryStateGenerationId generation = new(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            LibrarySnapshot snapshot = new(
                new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, LibraryRoot),
                Now,
                books,
                []);
            LibraryState state = new(
                generation,
                new(0),
                LibraryStateStatus.Authoritative,
                snapshot,
                Now,
                workflowCheckpoint: new(
                    LibraryWorkflowPhase.Completed,
                    generation,
                    new(0),
                    LibraryWorkflowPolicyVersions.Current,
                    Now,
                    new(generation, new(0))));
            FinalRecordDispositionAssessment disposition = new(
                [FinalRecordDispositionReasonCodes.PlaceholderLabel],
                [new(FinalRecordDispositionLabelSource.Title, "Unknown")]);
            QuarantineReviewSubject[] subjects = books.Select(value => new QuarantineReviewSubject(
                value.Id, value.Title, value.Authors,
                value.Formats.Select(item => new FinalAuthorFormatIdentity(
                    item.Format, item.ExpectedRelativePath, item.FileStatus, item.Fingerprint)),
                disposition, inputIdentity: FinalRecordInputIdentity.Create(value))).ToArray();
            QuarantineReviewWorkspace workspace = new(
                LibraryRoot,
                "C:\\Libraries\\Source - Unresolvable",
                generation,
                new(0),
                subjects,
                new(destinationExists, destinationExists ? "99999999-8888-7777-6666-555555555555" : null));
            _request = new(LibraryRoot, generation, new(0), workspace, true);

            State = A.Fake<ILibraryStateSession>();
            A.CallTo(() => State.GetCurrent(LibraryRoot)).Returns(state);
            A.CallTo(() => State.DeferStateChanged(LibraryRoot)).Returns(A.Fake<IDisposable>());
            A.CallTo(() => State.BeginMutationBatchAsync(
                    LibraryRoot, A<LibraryStateMutationIntent>._, A<CancellationToken>._))
                .Returns(LibraryStateSessionOutcome.Success(state));
            A.CallTo(() => State.ApplyMutationBatchAsync(
                    LibraryRoot,
                    A<string>._,
                    A<IReadOnlyList<LibraryStateDelta>>._,
                    A<bool>._,
                    A<CancellationToken>._))
                .Invokes(call => AppliedDeltas.AddRange(
                    call.GetArgument<IReadOnlyList<LibraryStateDelta>>(2)!))
                .Returns(LibraryStateSessionOutcome.Success(state));
            A.CallTo(() => State.CheckpointAsync(LibraryRoot, A<CancellationToken>._))
                .Returns(LibraryStateSessionOutcome.Success(state));
            A.CallTo(() => State.MarkUncertainAsync(
                    LibraryRoot, A<LibraryStateUncertainty>._, A<CancellationToken>._))
                .Returns(LibraryStateSessionOutcome.Success(state));

            ICalibreMutationWorkerSession worker = A.Fake<ICalibreMutationWorkerSession>();
            A.CallTo(() => worker.ExecuteChunkAsync(
                    A<CalibreMutationChunkRequest>._, A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                    Chunks.Add(chunk);
                    List<CalibreMutationOperationResult> results = [];
                    foreach (CalibreMutationOperation operation in chunk.Operations)
                    {
                        if (copyFails && operation.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine
                                && operation.RecordId.Value == copyFailureRecord
                            || deleteFails && operation.Kind == CalibreMutationOperationKind.RemoveRecordWithContent)
                        {
                            results.Add(new(operation.OperationId, operation.Kind, false, "controlled_failure"));
                            return Task.FromResult(new CalibreMutationChunkResult(
                                chunk.ChunkId, true, results, "controlled_failure"));
                        }
                        results.Add(new(
                            operation.OperationId, operation.Kind, true,
                            QuarantineRecordId: operation.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine
                                ? new(1000 + operation.RecordId.Value) : null));
                    }
                    return Task.FromResult(new CalibreMutationChunkResult(
                        chunk.ChunkId, true, results));
                });
            ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
            A.CallTo(() => workers.TryOpenAsync(
                    A<OpenCalibreMutationWorkerRequest>._, A<CancellationToken>._))
                .Returns(new CalibreMutationWorkerOpenResult(
                    worker,
                    null,
                    "99999999-8888-7777-6666-555555555555",
                    destinationExists));
            ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
            CalibreToolDescriptor tool = new(
                "C:\\Calibre2\\calibredb.exe",
                new(
                    "C:\\Calibre2\\calibredb.exe",
                    "9.12.0",
                    new(new string('f', 64)),
                    "calibredb/windows/9.12.0"));
            A.CallTo(() => tools.DiscoverAndProbeAsync(LibraryRoot, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(tool, []));
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            ILibraryMutationLeaseHandle handle = A.Fake<ILibraryMutationLeaseHandle>();
            A.CallTo(() => handle.IsHeld).Returns(true);
            A.CallTo(() => lease.TryAcquireAsync(
                    A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(handle, []));
            ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
            A.CallTo(() => ids.Create()).Returns(new(
                Guid.Parse("11111111-2222-3333-4444-555555555555")));
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddSeconds(1));
            _useCase = new(State, tools, workers, lease, ids, clock);
        }

        public string LibraryRoot { get; }
        public ILibraryStateSession State { get; }
        public List<CalibreMutationChunkRequest> Chunks { get; } = [];
        public List<LibraryStateDelta> AppliedDeltas { get; } = [];

        public Task<QuarantineCleanupResult> ExecuteAsync() => _useCase.ExecuteAsync(
            _request,
            null,
            CancellationToken.None);
    }
}
