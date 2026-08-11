using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteBulkExactDuplicateCleanupUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly FormatFileFingerprint Duplicate = Fingerprint('a', 10);

    [Fact]
    public async Task ComplementaryFormatMovesBeforeSourceRecordIsDeleted()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2,
            [Format(2, "EPUB", Duplicate), Format(2, "PDF", Fingerprint('b', 20))]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.IsCompleted.Should().BeTrue(string.Join("; ",
            result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        result.RemovedFormatCount.Should().Be(2);
        result.MergedRecordCount.Should().Be(1);
        result.RemovedRecordCount.Should().Be(1);
        harness.Logger.Entries.Should().Contain(value => value.Level == LogLevel.Information
            && value.Message.Contains("Outcome=Completed", StringComparison.Ordinal)
            && value.Message.Contains("RemovedFormats=2", StringComparison.Ordinal)
            && value.Message.Contains("MergedRecords=1", StringComparison.Ordinal)
            && value.Message.Contains("RemovedRecords=1", StringComparison.Ordinal)
            && value.Message.Contains("TotalMilliseconds=", StringComparison.Ordinal));
        harness.Trace.Should().ContainInOrder("transfer:2:1:PDF",
            "remove-format:2:EPUB", "remove-format:2:PDF", "remove-record:2");
        LibrarySnapshot final = harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot;
        final.Books.Should().ContainSingle().Which.Formats.Select(value => value.Format)
            .Should().Equal("EPUB", "PDF");
    }

    [Fact]
    public async Task NonIdenticalSameFormatConflictLeavesSourceRecordIntact()
    {
        CalibreBook keeper = Book(1,
            [Format(1, "EPUB", Duplicate), Format(1, "PDF", Fingerprint('b', 20))]);
        CalibreBook source = Book(2,
            [Format(2, "EPUB", Duplicate), Format(2, "PDF", Fingerprint('c', 30))]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.IsCompleted.Should().BeTrue();
        result.RemovedFormatCount.Should().Be(1);
        result.MergedRecordCount.Should().Be(0);
        result.RemovedRecordCount.Should().Be(0);
        result.SkippedRecordCount.Should().Be(1);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot.Books
            .Single(value => value.Id == source.Id).Formats.Should().ContainSingle(value => value.Format == "PDF");
    }

    [Fact]
    public async Task CompetingIncomingFormatsMergeOnlyFirstNonConflictingSource()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook firstSource = Book(2,
            [Format(2, "EPUB", Duplicate), Format(2, "MOBI", Fingerprint('b', 20))]);
        CalibreBook conflictingSource = Book(3,
            [Format(3, "EPUB", Duplicate), Format(3, "MOBI", Fingerprint('c', 30))]);
        Harness harness = await Harness.CreateAsync([keeper, firstSource, conflictingSource]);
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.MergedRecordCount.Should().Be(1);
        result.SkippedRecordCount.Should().Be(1);
        LibrarySnapshot final = harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot;
        final.Books.Select(value => value.Id).Should().Equal(new CalibreBookId(1), new CalibreBookId(3));
        final.Books.Single(value => value.Id == new CalibreBookId(3)).Formats
            .Should().ContainSingle(value => value.Format == "MOBI" && value.Fingerprint == Fingerprint('c', 30));
    }

    [Fact]
    public async Task StaleKeeperSelectionIsSkippedWithoutMutation()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2, [Format(2, "EPUB", Duplicate)]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();
        ExactBinaryDuplicateMember stale = new(new(99), "EPUB", "missing/book.epub");

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync([new(group.Id, stale)]);

        result.State.Should().Be(BulkExactDuplicateCleanupState.NothingToDo);
        result.Issues.Should().Contain(value => value.Code == "BULK_EXACT.GROUP_SELECTION_MISSING");
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Revision.Value.Should().Be(0);
    }

    [Fact]
    public async Task FailedWorkerChunkMarksStateUncertainAndDoesNotDeleteRecord()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2, [Format(2, "EPUB", Duplicate)]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();
        ICalibreMutationWorkerSession session = A.Fake<ICalibreMutationWorkerSession>();
        A.CallTo(() => session.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                    chunk.Operations.Select(value => new CalibreMutationOperationResult(
                        value.OperationId, value.Kind, false, "controlled_failure")).ToArray(),
                    "controlled_failure"));
            });
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreMutationWorkerOpenResult(session, null));

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.State.Should().Be(BulkExactDuplicateCleanupState.PartiallyCompleted);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Revision.Value.Should().Be(0);
    }

    [Fact]
    public async Task BlockingWorkerPreflightDoesNotFallBackToCli()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2, [Format(2, "EPUB", Duplicate)]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreMutationWorkerOpenResult(null, "CALIBRE_WORKER_IDENTITY_MISMATCH"));
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.State.Should().Be(BulkExactDuplicateCleanupState.PreflightFailed);
        result.Issues.Should().Contain(value => value.Code == "BULK_EXACT.WORKER_UNAVAILABLE");
    }

    [Fact]
    public async Task FailedWorkerChunkDoesNotProjectSuccessfulPrefix()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2,
            [Format(2, "EPUB", Duplicate), Format(2, "PDF", Fingerprint('b', 20))]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ICalibreMutationWorkerSession session = A.Fake<ICalibreMutationWorkerSession>();
        A.CallTo(() => session.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                CalibreMutationOperation transfer = chunk.Operations[0];
                return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                [
                    new(transfer.OperationId, transfer.Kind, true),
                    .. chunk.Operations.Skip(1).Select(value => new CalibreMutationOperationResult(
                        value.OperationId, value.Kind, false, "controlled_failure")),
                ], "controlled_failure"));
            });
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreMutationWorkerOpenResult(session, null));
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.State.Should().Be(BulkExactDuplicateCleanupState.PartiallyCompleted);
        LibraryState final = harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!;
        final.Status.Should().Be(LibraryStateStatus.Uncertain);
        final.Revision.Value.Should().Be(0);
        final.Snapshot.Books.Single(value => value.Id == keeper.Id).Formats
            .Should().NotContain(value => value.Format == "PDF");
    }

    [Fact]
    public async Task PersistentWorkerUsesChunksOfAtMostOneHundredOperations()
    {
        CalibreBook[] books = Enumerable.Range(1, 101)
            .Select(value => Book(value, [Format(value, "EPUB", Duplicate)]))
            .ToArray();
        Harness harness = await Harness.CreateAsync(books);
        List<int> chunkSizes = [];
        List<LibraryState> published = [];
        harness.State.StateChanged += (_, eventArgs) => published.Add(eventArgs.State);
        ICalibreMutationWorkerSession session = A.Fake<ICalibreMutationWorkerSession>();
        A.CallTo(() => session.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                chunkSizes.Add(chunk.Operations.Count);
                return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                    chunk.Operations.Select(value => new CalibreMutationOperationResult(
                        value.OperationId, value.Kind, true)).ToArray()));
            });
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreMutationWorkerOpenResult(session, null));
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == new CalibreBookId(1)))]);

        result.IsCompleted.Should().BeTrue(string.Join("; ",
            result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        result.RemovedFormatCount.Should().Be(100);
        result.RemovedRecordCount.Should().Be(100);
        chunkSizes.Should().Equal(100, 100);
        published.Should().ContainSingle().Which.Revision.Value.Should().Be(200);
        A.CallTo(() => harness.Store.WriteMutationIntentAsync(A<string>._,
            A<LibraryStateMutationIntent>.That.Matches(value => value.OperationCount == 200),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Store.AppendDeltaBatchAsync(A<string>._,
            A<IReadOnlyList<LibraryStateDelta>>.That.Matches(value => value.Count == 100),
            A<LibraryState>._, false, A<string>._, A<bool>._,
            A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => harness.Store.CompactAsync(A<string>._, A<LibraryState>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PersistentWorkerBypassesStagingAndCommandGateway()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2,
            [Format(2, "EPUB", Duplicate), Format(2, "PDF", Fingerprint('b', 20))]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ICalibreMutationWorkerSession session = A.Fake<ICalibreMutationWorkerSession>();
        A.CallTo(() => session.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                harness.Trace.AddRange(chunk.Operations.Select(value => $"worker:{value.Kind}"));
                return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                    chunk.Operations.Select(value => new CalibreMutationOperationResult(
                        value.OperationId, value.Kind, true)).ToArray()));
            });
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreMutationWorkerOpenResult(session, null));
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.IsCompleted.Should().BeTrue();
        harness.Trace.Should().Equal("worker:TransferFormat", "worker:RemoveFormat",
            "worker:RemoveFormat", "worker:RemoveRecord");
    }

    [Fact]
    public async Task AmbiguousWorkerChunkMarksStateUncertainWithoutCliRetry()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2, [Format(2, "EPUB", Duplicate)]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ICalibreMutationWorkerSession session = A.Fake<ICalibreMutationWorkerSession>();
        A.CallTo(() => session.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                    chunk.Operations.Select(value => new CalibreMutationOperationResult(
                        value.OperationId, value.Kind, false, "controlled_failure")).ToArray(),
                    "controlled_failure"));
            });
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreMutationWorkerOpenResult(session, null));
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.State.Should().Be(BulkExactDuplicateCleanupState.PartiallyCompleted);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        harness.Logger.Entries.Should().Contain(value => value.Level == LogLevel.Error
            && value.Message.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", StringComparison.Ordinal)
            && value.Message.Contains("controlled_failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingExternalBackupConfirmationBlocksBeforeWorkerStartup()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2, [Format(2, "EPUB", Duplicate)]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))],
            externalBackupConfirmed: false);

        result.State.Should().Be(BulkExactDuplicateCleanupState.PreflightFailed);
        result.Issues.Should().Contain(value => value.Code == "BULK_EXACT.BACKUP_NOT_CONFIRMED");
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    private sealed class Harness
    {
        private readonly ExecuteBulkExactDuplicateCleanupUseCase _useCase;

        private Harness(
            LibrarySnapshot snapshot,
            LibraryStateSession state,
            ILibraryStateStore store,
            ICalibreMutationWorkerFactory workers,
            ExecuteBulkExactDuplicateCleanupUseCase useCase,
            CapturingLogger<ExecuteBulkExactDuplicateCleanupUseCase> logger,
            List<string> trace)
        {
            Snapshot = snapshot;
            State = state;
            Store = store;
            Workers = workers;
            Logger = logger;
            _useCase = useCase;
            Trace = trace;
        }

        public LibrarySnapshot Snapshot { get; }
        public LibraryStateSession State { get; }
        public ILibraryStateStore Store { get; }
        public ICalibreMutationWorkerFactory Workers { get; }
        public CapturingLogger<ExecuteBulkExactDuplicateCleanupUseCase> Logger { get; }
        public List<string> Trace { get; }

        public static async Task<Harness> CreateAsync(CalibreBook[] books)
        {
            LibrarySnapshot snapshot = new(
                new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
                Now, books, [], ExactBinaryDuplicateDetector.Detect(books));
            ILibraryStateStore store = A.Fake<ILibraryStateStore>();
            LibraryStateSession state = new(store);
            await state.StartFromScanAsync(snapshot, CancellationToken.None);
            List<string> trace = [];
            ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
            ICalibreMutationWorkerSession workerSession = A.Fake<ICalibreMutationWorkerSession>();
            A.CallTo(() => workerSession.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                    A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                    foreach (CalibreMutationOperation operation in chunk.Operations)
                    {
                        trace.Add(operation.Kind switch
                        {
                            CalibreMutationOperationKind.TransferFormat =>
                                $"transfer:{operation.RecordId.Value}:{operation.TargetRecordId!.Value.Value}:{operation.CanonicalFormat}",
                            CalibreMutationOperationKind.RemoveFormat =>
                                $"remove-format:{operation.RecordId.Value}:{operation.CanonicalFormat}",
                            CalibreMutationOperationKind.RemoveRecord => $"remove-record:{operation.RecordId.Value}",
                            _ => operation.Kind.ToString(),
                        });
                    }
                    return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                        chunk.Operations.Select(value => new CalibreMutationOperationResult(
                            value.OperationId, value.Kind, true)).ToArray()));
                });
            A.CallTo(() => workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                    A<CancellationToken>._))
                .Returns(new CalibreMutationWorkerOpenResult(workerSession, null));
            ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
            CalibreToolDescriptor tool = new("C:\\Calibre2\\calibredb.exe",
                new("C:\\Calibre2\\calibredb.exe", "9.11.0", new(new string('f', 64)),
                    "calibredb/windows/9.11.0"));
            A.CallTo(() => tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(tool, []));
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            ILibraryMutationLeaseHandle handle = A.Fake<ILibraryMutationLeaseHandle>();
            A.CallTo(() => handle.IsHeld).Returns(true);
            A.CallTo(() => lease.TryAcquireAsync(A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(handle, []));
            ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
            A.CallTo(() => ids.Create()).Returns(new CleanupExecutionId(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddSeconds(1));
            CapturingLogger<ExecuteBulkExactDuplicateCleanupUseCase> logger = new();
            ExecuteBulkExactDuplicateCleanupUseCase useCase = new(
                state, tools, workers, lease, ids, clock, logger);
            return new(snapshot, state, store, workers, useCase, logger, trace);
        }

        public Task<BulkExactDuplicateCleanupResult> ExecuteAsync(
            IReadOnlyList<ExactDuplicateKeeperSelection> selections,
            bool externalBackupConfirmed = true) => _useCase.ExecuteAsync(new(
            Snapshot.Identity.LibraryRoot, selections, externalBackupConfirmed), null, CancellationToken.None);
    }

    private static CalibreBook Book(long id, IEnumerable<BookFormat> formats) => new(
        new(id), "Book", "Author", [new(new(id), "Author", "Author")], [], formats,
        $"Author/Book ({id})");

    private static BookFormat Format(long recordId, string format, FormatFileFingerprint fingerprint) => new(
        format, "book", $"Author/Book ({recordId})/book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present, fingerprint,
        new(fingerprint.SizeInBytes, Now, Now, 0));

    private static FormatFileFingerprint Fingerprint(char value, long size) => new(
        size, new(new string(value, 64)));

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
