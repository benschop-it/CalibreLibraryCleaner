using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteBulkMetadataCandidateCleanupUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TransfersComplementaryFormatAndRemovesConflictingSourceRecord()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", 'a')]);
        CalibreBook source = Book(2, [Format(2, "EPUB", 'b'), Format(2, "PDF", 'c')]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactMetadataDuplicateGroup group = harness.Snapshot.ExactMetadataDuplicateGroups.Single();

        BulkMetadataCandidateCleanupResult result = await harness.ExecuteAsync([
            new(group.Id, keeper.Id, Skip: false),
        ]);

        result.IsCompleted.Should().BeTrue(string.Join("; ",
            result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        result.TransferredFormatCount.Should().Be(1);
        result.RemovedFormatCount.Should().Be(2);
        result.RemovedRecordCount.Should().Be(1);
        harness.OperationTrace.Should().Equal(
            "TransferFormat:2:1:PDF",
            "RemoveFormat:2::EPUB",
            "RemoveFormat:2::PDF",
            "RemoveRecord:2::");
        LibrarySnapshot final = harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot;
        final.Books.Should().ContainSingle().Which.Formats.Select(value => (value.Format, value.Fingerprint))
            .Should().BeEquivalentTo([
                ("EPUB", keeper.Formats.Single().Fingerprint),
                ("PDF", source.Formats.Single(value => value.Format == "PDF").Fingerprint),
            ]);
    }

    [Fact]
    public async Task SkippedGroupDoesNotOpenWorker()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", 'a')]);
        CalibreBook source = Book(2, [Format(2, "EPUB", 'b')]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactMetadataDuplicateGroup group = harness.Snapshot.ExactMetadataDuplicateGroups.Single();

        BulkMetadataCandidateCleanupResult result = await harness.ExecuteAsync([
            new(group.Id, keeper.Id, Skip: true),
        ]);

        result.State.Should().Be(BulkMetadataCandidateCleanupState.NothingToDo);
        result.SkippedGroupCount.Should().Be(1);
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task BackupMustBeConfirmedBeforePlanning()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", 'a')]);
        CalibreBook source = Book(2, [Format(2, "EPUB", 'b')]);
        Harness harness = await Harness.CreateAsync([keeper, source]);
        ExactMetadataDuplicateGroup group = harness.Snapshot.ExactMetadataDuplicateGroups.Single();

        BulkMetadataCandidateCleanupResult result = await harness.ExecuteAsync([
            new(group.Id, keeper.Id, Skip: false),
        ], externalBackupConfirmed: false);

        result.State.Should().Be(BulkMetadataCandidateCleanupState.PreflightFailed);
        result.Issues.Should().Contain(value => value.Code == "BULK_METADATA.BACKUP_NOT_CONFIRMED");
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task UnresolvedComplementaryFormatSkipsGroupBeforeWorkerStartup()
    {
        CalibreBook keeper = Book(1, [Format(1, "EPUB", 'a')]);
        CalibreBook firstSource = Book(2, [Format(2, "PDF", 'b')]);
        CalibreBook conflictingSource = Book(3, [Format(3, "PDF", 'c')]);
        Harness harness = await Harness.CreateAsync([keeper, firstSource, conflictingSource]);
        ExactMetadataDuplicateGroup group = harness.Snapshot.ExactMetadataDuplicateGroups.Single();

        BulkMetadataCandidateCleanupResult result = await harness.ExecuteAsync([
            new(group.Id, keeper.Id, Skip: false),
        ]);

        result.State.Should().Be(BulkMetadataCandidateCleanupState.NothingToDo);
        result.SkippedGroupCount.Should().Be(1);
        result.Issues.Should().Contain(value => value.Code == "BULK_METADATA.COMPLEMENTARY_FORMAT_UNRESOLVED");
        A.CallTo(() => harness.Workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    private static CalibreBook Book(long id, IEnumerable<BookFormat> formats) => new(
        new(id), "Shared Book", "Author", [new(new(id), "Author", "Author")],
        [new("isbn", $"97803064061{id:D2}")], formats, $"Author/Shared Book ({id})",
        new(publisher: id == 1 ? "Complete Publisher" : null,
            languages: id == 1 ? ["eng"] : [], hasCover: id == 1));

    private static BookFormat Format(long recordId, string format, char digest) => new(
        format, "book", $"Author/Shared Book ({recordId})/book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        new(10, new(new string(digest, 64))),
        new(10, Now, Now, 0));

    private sealed class Harness
    {
        private readonly ExecuteBulkMetadataCandidateCleanupUseCase _useCase;

        private Harness(
            LibrarySnapshot snapshot,
            LibraryStateSession state,
            ICalibreMutationWorkerFactory workers,
            ExecuteBulkMetadataCandidateCleanupUseCase useCase,
            List<string> operationTrace)
        {
            Snapshot = snapshot;
            State = state;
            Workers = workers;
            _useCase = useCase;
            OperationTrace = operationTrace;
        }

        public LibrarySnapshot Snapshot { get; }
        public LibraryStateSession State { get; }
        public ICalibreMutationWorkerFactory Workers { get; }
        public List<string> OperationTrace { get; }

        public static async Task<Harness> CreateAsync(CalibreBook[] books)
        {
            LibraryIdentity identity = new(
                "87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library");
            ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
            ConsolidationRecommendation recommendation = new ConsolidationRecommendationPolicy().Generate(
                identity, group, books, [], [], [], CancellationToken.None);
            LibrarySnapshot snapshot = new(identity, Now, books, [], [], [group], [], [recommendation]);
            ILibraryStateStore store = A.Fake<ILibraryStateStore>();
            LibraryStateSession state = new(store);
            await state.StartFromScanAsync(snapshot, CancellationToken.None);
            List<string> trace = [];
            ICalibreMutationWorkerSession session = A.Fake<ICalibreMutationWorkerSession>();
            A.CallTo(() => session.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                    A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                    trace.AddRange(chunk.Operations.Select(value =>
                        $"{value.Kind}:{value.RecordId.Value}:{value.TargetRecordId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}:{value.CanonicalFormat ?? string.Empty}"));
                    return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                        chunk.Operations.Select(value => new CalibreMutationOperationResult(
                            value.OperationId, value.Kind, true)).ToArray()));
                });
            ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
            A.CallTo(() => workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                    A<CancellationToken>._))
                .Returns(new CalibreMutationWorkerOpenResult(session, null));
            ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
            CalibreToolDescriptor tool = new("C:\\Calibre2\\calibredb.exe",
                new("C:\\Calibre2\\calibredb.exe", "9.11.0", new(new string('f', 64)),
                    "calibredb/windows/9.11.0"));
            A.CallTo(() => tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(tool, []));
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            ILibraryMutationLeaseHandle leaseHandle = A.Fake<ILibraryMutationLeaseHandle>();
            A.CallTo(() => leaseHandle.IsHeld).Returns(true);
            A.CallTo(() => lease.TryAcquireAsync(A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(leaseHandle, []));
            ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
            A.CallTo(() => ids.Create()).Returns(new CleanupExecutionId(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddSeconds(1));
            ExecuteBulkMetadataCandidateCleanupUseCase useCase = new(
                state, tools, workers, lease, ids, clock);
            return new(snapshot, state, workers, useCase, trace);
        }

        public Task<BulkMetadataCandidateCleanupResult> ExecuteAsync(
            IReadOnlyList<MetadataCandidateCleanupSelection> selections,
            bool externalBackupConfirmed = true) => _useCase.ExecuteAsync(new(
                Snapshot.Identity.LibraryRoot, selections, externalBackupConfirmed),
                null, CancellationToken.None);
    }
}
