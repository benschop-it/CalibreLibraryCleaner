using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
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

        result.IsCompleted.Should().BeTrue();
        result.RemovedFormatCount.Should().Be(2);
        result.MergedRecordCount.Should().Be(1);
        result.RemovedRecordCount.Should().Be(1);
        harness.Trace.Should().ContainInOrder("stage:2:PDF", "add:1:PDF",
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
        A.CallTo(() => harness.Staging.StageAsync(A<CleanupExecutionId>._, A<string>._,
            A<CalibreBookId>._, A<BookFormat>._, A<CancellationToken>._)).MustNotHaveHappened();
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
    public async Task FailedRemovalMarksStateUncertainAndDoesNotDeleteRecord()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", Duplicate)]);
        CalibreBook source = Book(2, [Format(2, "EPUB", Duplicate)]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactBinaryDuplicateGroup group = harness.Snapshot.ExactBinaryDuplicateGroups.Single();
        A.CallTo(() => harness.Commands.RemoveFormatAsync(A<RemoveCalibreFormatRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreCommandResult("remove_format", true, 1, [], string.Empty,
                "failed", TimeSpan.Zero, "CONTROLLED_FAILURE"));

        BulkExactDuplicateCleanupResult result = await harness.ExecuteAsync(
            [new(group.Id, group.Members.Single(value => value.BookId == keeper.Id))]);

        result.State.Should().Be(BulkExactDuplicateCleanupState.PartiallyCompleted);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
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
        result.Issues.Should().Contain(value => value.Code == "BULK_EXACT.WORKER_PREFLIGHT_BLOCKED");
        A.CallTo(() => harness.Commands.RemoveFormatAsync(
            A<RemoveCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Staging.StageAsync(A<CleanupExecutionId>._, A<string>._,
            A<CalibreBookId>._, A<BookFormat>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SuccessfulWorkerPrefixIsProjectedBeforeChunkBecomesUncertain()
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
        final.Revision.Value.Should().Be(1);
        final.Snapshot.Books.Single(value => value.Id == keeper.Id).Formats
            .Should().Contain(value => value.Format == "PDF");
        A.CallTo(() => harness.Commands.RemoveFormatAsync(
            A<RemoveCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
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

        result.IsCompleted.Should().BeTrue();
        result.RemovedFormatCount.Should().Be(100);
        result.RemovedRecordCount.Should().Be(100);
        chunkSizes.Should().Equal(100, 100);
        published.Should().ContainSingle().Which.Revision.Value.Should().Be(200);
        A.CallTo(() => harness.Store.WriteMutationIntentAsync(A<string>._,
            A<LibraryStateMutationIntent>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => harness.Store.AppendDeltaBatchAsync(A<string>._,
            A<IReadOnlyList<LibraryStateDelta>>.That.Matches(value => value.Count == 100),
            A<LibraryState>._, false, A<string>._, true,
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
        A.CallTo(() => harness.Staging.StageAsync(A<CleanupExecutionId>._, A<string>._,
            A<CalibreBookId>._, A<BookFormat>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Commands.AddOrReplaceFormatAsync(
            A<AddOrReplaceCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Commands.RemoveFormatAsync(
            A<RemoveCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Commands.RemoveRecordAsync(
            A<RemoveCalibreRecordRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
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
        A.CallTo(() => harness.Commands.RemoveFormatAsync(
            A<RemoveCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    private sealed class Harness
    {
        private readonly ExecuteBulkExactDuplicateCleanupUseCase _useCase;

        private Harness(
            LibrarySnapshot snapshot,
            LibraryStateSession state,
            ILibraryStateStore store,
            IExactDuplicateFormatStaging staging,
            ICalibreCommandGateway commands,
            ICalibreMutationWorkerFactory workers,
            ExecuteBulkExactDuplicateCleanupUseCase useCase,
            List<string> trace)
        {
            Snapshot = snapshot;
            State = state;
            Store = store;
            Staging = staging;
            Commands = commands;
            Workers = workers;
            _useCase = useCase;
            Trace = trace;
        }

        public LibrarySnapshot Snapshot { get; }
        public LibraryStateSession State { get; }
        public ILibraryStateStore Store { get; }
        public IExactDuplicateFormatStaging Staging { get; }
        public ICalibreCommandGateway Commands { get; }
        public ICalibreMutationWorkerFactory Workers { get; }
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
            IExactDuplicateFormatStaging staging = A.Fake<IExactDuplicateFormatStaging>();
            A.CallTo(() => staging.StageAsync(A<CleanupExecutionId>._, A<string>._,
                    A<CalibreBookId>._, A<BookFormat>._, A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    CalibreBookId recordId = call.GetArgument<CalibreBookId>(2);
                    BookFormat format = call.GetArgument<BookFormat>(3)!;
                    trace.Add($"stage:{recordId.Value}:{format.Format}");
                    return Task.FromResult(new StagedExactDuplicateFormat(recordId, format.Format,
                        $"C:\\stage\\{recordId.Value}\\book.{format.Format.ToLowerInvariant()}",
                        format.Fingerprint!));
                });
            ICalibreCommandGateway commands = A.Fake<ICalibreCommandGateway>();
            A.CallTo(() => commands.AddOrReplaceFormatAsync(A<AddOrReplaceCalibreFormatRequest>._,
                    A<CancellationToken>._))
                .Invokes(call =>
                {
                    AddOrReplaceCalibreFormatRequest value = call.GetArgument<AddOrReplaceCalibreFormatRequest>(0)!;
                    trace.Add($"add:{value.TargetRecordId.Value}:{value.CanonicalFormat}");
                }).Returns(Command("add_format"));
            A.CallTo(() => commands.RemoveFormatAsync(A<RemoveCalibreFormatRequest>._,
                    A<CancellationToken>._))
                .Invokes(call =>
                {
                    RemoveCalibreFormatRequest value = call.GetArgument<RemoveCalibreFormatRequest>(0)!;
                    trace.Add($"remove-format:{value.RecordId.Value}:{value.CanonicalFormat}");
                }).Returns(Command("remove_format"));
            A.CallTo(() => commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._,
                    A<CancellationToken>._))
                .Invokes(call => trace.Add($"remove-record:{call.GetArgument<RemoveCalibreRecordRequest>(0)!.RecordId.Value}"))
                .Returns(Command("remove"));
            ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
            A.CallTo(() => workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                    A<CancellationToken>._))
                .Returns(new CalibreMutationWorkerOpenResult(null, "CONTROLLED_UNAVAILABLE", true));
            ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
            CalibreToolDescriptor tool = new("C:\\Calibre2\\calibredb.exe",
                new("C:\\Calibre2\\calibredb.exe", "9.11.0", new(new string('f', 64)),
                    "calibredb/windows/9.11.0"), Enum.GetValues<CalibreExecutionCapability>());
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
            ExecuteBulkExactDuplicateCleanupUseCase useCase = new(state, tools, commands,
                workers, staging, lease, ids, clock);
            return new(snapshot, state, store, staging, commands, workers, useCase, trace);
        }

        public Task<BulkExactDuplicateCleanupResult> ExecuteAsync(
            IReadOnlyList<ExactDuplicateKeeperSelection> selections) => _useCase.ExecuteAsync(new(
            Snapshot.Identity.LibraryRoot, selections), null, CancellationToken.None);
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

    private static CalibreCommandResult Command(string kind) => new(
        kind, true, 0, [], string.Empty, string.Empty, TimeSpan.FromMilliseconds(1));
}
