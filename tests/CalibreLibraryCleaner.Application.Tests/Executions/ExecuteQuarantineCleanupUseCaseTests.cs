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

    private sealed class Harness
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        private readonly ExecuteQuarantineCleanupUseCase _useCase;
        private readonly ExecuteQuarantineCleanupRequest _request;

        public Harness(bool copyFails)
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
            LibraryStateGenerationId generation = new(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            LibrarySnapshot snapshot = new(
                new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, LibraryRoot),
                Now,
                [book],
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
            QuarantineReviewSubject subject = new(
                book.Id,
                book.Title,
                book.Authors,
                [new(format.Format, format.ExpectedRelativePath, format.FileStatus, format.Fingerprint)],
                disposition);
            QuarantineReviewWorkspace workspace = new(
                LibraryRoot,
                "C:\\Libraries\\Source - Unresolvable",
                generation,
                new(0),
                [subject],
                new(false, null));
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
                    CalibreMutationOperation operation = chunk.Operations.Single();
                    if (copyFails && operation.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine)
                        return Task.FromResult(new CalibreMutationChunkResult(
                            chunk.ChunkId,
                            true,
                            [new(operation.OperationId, operation.Kind, false, "quarantine_copy_failed")],
                            "quarantine_copy_failed"));
                    return Task.FromResult(new CalibreMutationChunkResult(
                        chunk.ChunkId,
                        true,
                        [new(
                            operation.OperationId,
                            operation.Kind,
                            true,
                            VerifiedManagedPath: operation.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine
                                ? "Author/Book (1001)"
                                : null,
                            VerifiedFormats: operation.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine
                                ? [new("EPUB", "book.epub", fingerprint)]
                                : null,
                            QuarantineRecordId: operation.Kind == CalibreMutationOperationKind.CopyRecordToQuarantine
                                ? new(1001)
                                : null)]));
                });
            ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
            A.CallTo(() => workers.TryOpenAsync(
                    A<OpenCalibreMutationWorkerRequest>._, A<CancellationToken>._))
                .Returns(new CalibreMutationWorkerOpenResult(
                    worker,
                    null,
                    "99999999-8888-7777-6666-555555555555",
                    false));
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
