using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteCompositeCleanupUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConflictingKeepersStopBeforeWorkerStartup()
    {
        Harness harness = await Harness.CreateAsync();

        CompositeCleanupResult result = await harness.UseCase.ExecuteAsync(new(
            harness.Snapshot.Identity.LibraryRoot,
            [],
            [new(harness.Metadata.Id, harness.First.Id, Skip: false, KeeperWasOverridden: true)],
            [new(harness.Expanded.Id, harness.Second.Id, Skip: false, KeeperWasOverridden: true)],
            ExternalBackupConfirmed: true), null, CancellationToken.None);

        result.State.Should().Be(CompositeCleanupState.Conflict);
        result.Conflicts.Should().NotBeEmpty();
        A.CallTo(() => harness.Workers.TryOpenAsync(
            A<OpenCalibreMutationWorkerRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CompatibleThreeCategoryOverlapUsesOneWorkerAndCanonicalOrder()
    {
        Harness harness = await Harness.CreateAsync();
        ExactBinaryDuplicateMember retained = harness.Exact.Members.Single(value =>
            value.BookId == harness.First.Id);

        CompositeCleanupResult result = await harness.UseCase.ExecuteAsync(new(
            harness.Snapshot.Identity.LibraryRoot,
            [new(harness.Exact.Id, retained)],
            [new(harness.Metadata.Id, harness.First.Id, Skip: false)],
            [new(harness.Expanded.Id, harness.First.Id, Skip: false)],
            ExternalBackupConfirmed: true), null, CancellationToken.None);

        result.IsCompleted.Should().BeTrue(string.Join("; ", result.Issues.Select(value => value.Code)));
        result.RemovedFormatCount.Should().Be(1);
        result.RemovedRecordCount.Should().Be(1);
        harness.Trace.Should().Equal(
            "RemoveFormat:2::EPUB",
            "RemoveRecord:2::");
        A.CallTo(() => harness.Workers.TryOpenAsync(
            A<OpenCalibreMutationWorkerRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot.Books
            .Should().ContainSingle(value => value.Id == harness.First.Id);
    }

    private sealed record Harness(
        LibrarySnapshot Snapshot,
        CalibreBook First,
        CalibreBook Second,
        ExactBinaryDuplicateGroup Exact,
        ExactMetadataDuplicateGroup Metadata,
        WorkLanguageCandidateGroup Expanded,
        LibraryStateSession State,
        ICalibreMutationWorkerFactory Workers,
        ExecuteCompositeCleanupUseCase UseCase,
        List<string> Trace)
    {
        public static async Task<Harness> CreateAsync()
        {
            FormatFileFingerprint fingerprint = new(1_024, new(new string('a', 64)));
            CalibreBook first = Book(1, fingerprint);
            CalibreBook second = Book(2, fingerprint);
            ExactBinaryDuplicateGroup exact = ExactBinaryDuplicateDetector.Detect([first, second]).Single();
            ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect([first, second]).Single();
            WorkLanguageCandidateGroup expanded = WorkLanguageCandidateGroup.Create(
                "en", [first.Id, second.Id], [first.Id, second.Id],
                WorkLanguageCandidateConfidence.Strong,
                [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
                contentComparison: new(1, 1, 0, 0, 0, 0));
            LibrarySnapshot snapshot = new(
                new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
                Now, [first, second], [], [exact], [metadata],
                workLanguageCandidateGroups: [expanded],
                matchingRunSummary: new(MatchingPolicyVersion.Current,
                    MatchingEvidenceStatus.Available, 2, 1, 1, 0, 2, 0, 1, 1, 0));
            ILibraryStateStore store = A.Fake<ILibraryStateStore>();
            LibraryStateSession state = new(store);
            await state.StartFromScanAsync(snapshot, CancellationToken.None);
            List<string> trace = [];
            ICalibreMutationWorkerSession session = A.Fake<ICalibreMutationWorkerSession>();
            A.CallTo(() => session.ExecuteChunkAsync(
                    A<CalibreMutationChunkRequest>._, A<CancellationToken>._))
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
            A.CallTo(() => workers.TryOpenAsync(
                    A<OpenCalibreMutationWorkerRequest>._, A<CancellationToken>._))
                .Returns(new CalibreMutationWorkerOpenResult(session, null));
            ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
            A.CallTo(() => tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(new(
                    "C:\\Calibre2\\calibredb.exe",
                    new("C:\\Calibre2\\calibredb.exe", "9.11.0",
                        new(new string('f', 64)), "calibredb/windows/9.11.0")), []));
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            ILibraryMutationLeaseHandle handle = A.Fake<ILibraryMutationLeaseHandle>();
            A.CallTo(() => handle.IsHeld).Returns(true);
            A.CallTo(() => lease.TryAcquireAsync(
                    A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(handle, []));
            ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
            A.CallTo(() => ids.Create()).Returns(new(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddSeconds(1));
            ExecuteCompositeCleanupUseCase useCase = new(
                state, tools, workers, lease, ids, clock);
            return new(snapshot, first, second, exact, metadata, expanded,
                state, workers, useCase, trace);
        }

        private static CalibreBook Book(long id, FormatFileFingerprint fingerprint) => new(
            new(id), "Shared Book", "Author", [new(new(id), "Author", "Author")], [],
            [new("EPUB", "book", $"Author/Book{id}/book.epub", FormatFileStatus.Present,
                fingerprint, new(fingerprint.SizeInBytes, Now, Now, 0))],
            $"Author/Book{id}", new(languages: ["eng"]));
    }
}
