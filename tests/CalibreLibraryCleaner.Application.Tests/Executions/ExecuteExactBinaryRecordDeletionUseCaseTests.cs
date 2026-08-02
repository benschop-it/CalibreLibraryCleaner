using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteExactBinaryRecordDeletionUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteBackupAndConfirmationPrecedeFormatThenEmptyRecordRemoval()
    {
        Harness harness = Harness.Success();

        ExactBinaryRecordDeletionResult result = await harness.ExecuteAsync();

        result.IsCompleted.Should().BeTrue();
        result.RemovedFormatCount.Should().Be(1);
        result.RemovedRecordCount.Should().Be(1);
        A.CallTo(() => harness.Commands.ExportRecordAsync(A<ExportCalibreRecordRequest>._, A<CancellationToken>._))
            .MustHaveHappened(2, Times.Exactly);
        A.CallTo(() => harness.Confirmation.ConfirmAsync(harness.Plan,
            A<ExactBinaryRecordBackupManifest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Commands.RemoveFormatAsync(
            A<RemoveCalibreFormatRequest>.That.Matches(value => value.RecordId == new CalibreBookId(2)
                && value.CanonicalFormat == "EPUB"), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Commands.RemoveRecordAsync(
            A<RemoveCalibreRecordRequest>.That.Matches(value => value.RecordId == new CalibreBookId(2)),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        harness.Trace.Should().ContainInOrder("backup-inputs", "export", "export", "backup-sealed",
            "confirm", "snapshot-invalidated", "remove-format", "remove-record");
        harness.StateSession.GetCurrent("C:\\library")!.Revision.Should().Be(new LibraryStateRevision(2));
        harness.StateSession.GetCurrent("C:\\library")!.Snapshot.Books.Select(value => value.Id)
            .Should().Equal(new CalibreBookId(1), new CalibreBookId(3));
    }

    [Fact]
    public async Task BackupFailurePreventsRecordRemoval()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.RecordBackup.VerifyAndSealAsync(
                A<ExactBinaryRecordBackupInputs>._, harness.Plan, A<CancellationToken>._))
            .Returns(new ExactBinaryRecordBackupResult(null,
                [new("BINARY_BACKUP.FAILED", ExecutionIssueSeverity.BlockingError, "Failed.")]));

        ExactBinaryRecordDeletionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(ExactBinaryRecordDeletionState.BackupFailed);
        result.MutationStarted.Should().BeFalse();
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Commands.RemoveFormatAsync(A<RemoveCalibreFormatRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SnapshotInvalidationFailurePreventsRecordRemoval()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.SnapshotStore.DeleteAsync(A<string>._, A<CancellationToken>._))
            .Throws(new IOException("controlled"));

        ExactBinaryRecordDeletionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(ExactBinaryRecordDeletionState.PreflightFailed);
        result.Issues.Should().Contain(value => value.Code == "BINARY_EXECUTION.SNAPSHOT_INVALIDATION_FAILED");
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Commands.RemoveFormatAsync(A<RemoveCalibreFormatRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AmbiguousCommandMarksStateUncertainWithoutApplyingDelta()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.Commands.RemoveFormatAsync(
                A<RemoveCalibreFormatRequest>._, A<CancellationToken>._))
            .Returns(new CalibreCommandResult("remove_format", true, null, [], string.Empty,
                string.Empty, TimeSpan.FromMilliseconds(1), "COMMAND_RESULT_AMBIGUOUS"));

        ExactBinaryRecordDeletionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(ExactBinaryRecordDeletionState.PartiallyApplied);
        LibraryState state = harness.StateSession.GetCurrent("C:\\library")!;
        state.Status.Should().Be(LibraryStateStatus.Uncertain);
        state.Revision.Should().Be(new LibraryStateRevision(0));
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    private sealed class Harness
    {
        private readonly IClock _clock;
        private readonly ICleanupExecutionIdGenerator _ids;
        private readonly ILibraryMutationLease _lease;
        private readonly IExecutionBackupStore _workspaceStore;
        private readonly ICalibreToolDiscovery _tools;
        private readonly PersistedLibrarySnapshotsUseCase _persistedSnapshots;

        private Harness(
            ExactBinaryCleanupPlan plan,
            IClock clock,
            ICleanupExecutionIdGenerator ids,
            ILibraryMutationLease lease,
            IExecutionBackupStore workspaceStore,
            IExactBinaryRecordBackupStore recordBackup,
            LibraryStateSession stateSession,
            ICalibreToolDiscovery tools,
            PersistedLibrarySnapshotsUseCase persistedSnapshots,
            ILibrarySnapshotStore snapshotStore,
            ICalibreCommandGateway commands,
            IExactBinaryRecordDeletionConfirmation confirmation,
            List<string> trace)
        {
            Plan = plan;
            _clock = clock;
            _ids = ids;
            _lease = lease;
            _workspaceStore = workspaceStore;
            RecordBackup = recordBackup;
            StateSession = stateSession;
            _tools = tools;
            _persistedSnapshots = persistedSnapshots;
            SnapshotStore = snapshotStore;
            Commands = commands;
            Confirmation = confirmation;
            Trace = trace;
        }

        public ExactBinaryCleanupPlan Plan { get; }
        public IExactBinaryRecordBackupStore RecordBackup { get; }
        public LibraryStateSession StateSession { get; }
        public ICalibreCommandGateway Commands { get; }
        public IExactBinaryRecordDeletionConfirmation Confirmation { get; }
        public ILibrarySnapshotStore SnapshotStore { get; }
        public List<string> Trace { get; }

        public static Harness Success()
        {
            (ExactBinaryCleanupPlan plan, LibrarySnapshot before) = ApprovedPlan();
            List<string> trace = [];
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(Now);
            ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
            CleanupExecutionId executionId = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));
            A.CallTo(() => ids.Create()).Returns(executionId);
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            ILibraryMutationLeaseHandle handle = A.Fake<ILibraryMutationLeaseHandle>();
            A.CallTo(() => handle.IsHeld).Returns(true);
            A.CallTo(() => lease.TryAcquireAsync(A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(handle, []));
            IExecutionBackupStore workspaceStore = A.Fake<IExecutionBackupStore>();
            A.CallTo(() => workspaceStore.ValidateDestinationAsync(A<string>._, A<string>._, A<long>._,
                    A<CancellationToken>._))
                .Returns(new BackupDestinationValidation("C:\\backup", 1_000_000_000, []));
            ExecutionWorkspace workspace = new(executionId, "C:\\backup\\execution", "C:\\backup");
            A.CallTo(() => workspaceStore.CreateWorkspaceAsync(executionId, "C:\\backup", A<CancellationToken>._))
                .Returns(workspace);
            IExactBinaryRecordBackupStore recordBackup = A.Fake<IExactBinaryRecordBackupStore>();
            Dictionary<CalibreBookId, string> exports = plan.Definition.ExpectedRecords
                .ToDictionary(value => value.RecordId, value => $"C:\\backup\\exports\\{value.RecordId.Value}");
            A.CallTo(() => recordBackup.CreateInputsAsync(workspace, plan, A<string>._,
                    A<CalibreToolDescriptor>._, A<string>._, A<CancellationToken>._))
                .Invokes(() => trace.Add("backup-inputs"))
                .Returns(new ExactBinaryRecordBackupInputs(workspace, exports, []));
            ExactBinaryRecordBackupManifest manifest = new(executionId, plan.Id, plan.ContentDigest, Now, [],
                new Sha256Digest(new string('e', 64)));
            A.CallTo(() => recordBackup.VerifyAndSealAsync(A<ExactBinaryRecordBackupInputs>._, plan,
                    A<CancellationToken>._))
                .Invokes(() => trace.Add("backup-sealed"))
                .Returns(new ExactBinaryRecordBackupResult(manifest, []));
            A.CallTo(() => recordBackup.VerifyAvailableAsync(workspace, manifest, A<CancellationToken>._))
                .Returns([]);
            LibraryStateSession stateSession = new();
            stateSession.StartFromScan(before);
            CalibreToolDescriptor tool = new("C:\\Calibre2\\calibredb.exe",
                new("C:\\Calibre2\\calibredb.exe", "9.11.0", new(new string('f', 64)),
                    "calibredb/windows/9.11.0"), Enum.GetValues<CalibreExecutionCapability>());
            ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
            A.CallTo(() => tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(tool, []));
            ICalibreCommandGateway commands = A.Fake<ICalibreCommandGateway>();
            A.CallTo(() => commands.ExportRecordAsync(A<ExportCalibreRecordRequest>._, A<CancellationToken>._))
                .Invokes(() => trace.Add("export")).Returns(Command("export"));
            A.CallTo(() => commands.RemoveFormatAsync(A<RemoveCalibreFormatRequest>._, A<CancellationToken>._))
                .Invokes(() => trace.Add("remove-format")).Returns(Command("remove_format"));
            A.CallTo(() => commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._, A<CancellationToken>._))
                .Invokes(() => trace.Add("remove-record")).Returns(Command("remove"));
            IExactBinaryRecordDeletionConfirmation confirmation = A.Fake<IExactBinaryRecordDeletionConfirmation>();
            A.CallTo(() => confirmation.ConfirmAsync(plan, manifest, A<CancellationToken>._))
                .Invokes(() => trace.Add("confirm")).Returns(true);
            ILibrarySnapshotStore snapshotStore = A.Fake<ILibrarySnapshotStore>();
            A.CallTo(() => snapshotStore.DeleteAsync(A<string>._, A<CancellationToken>._))
                .Invokes(() => trace.Add("snapshot-invalidated"));
            PersistedLibrarySnapshotsUseCase persistedSnapshots = new(snapshotStore);
            return new(plan, clock, ids, lease, workspaceStore, recordBackup,
                stateSession, tools, persistedSnapshots, snapshotStore, commands, confirmation, trace);
        }

        public Task<ExactBinaryRecordDeletionResult> ExecuteAsync() => new ExecuteExactBinaryRecordDeletionUseCase(
            StateSession, _tools, Commands, _lease, _workspaceStore, RecordBackup, _ids, Confirmation,
            _persistedSnapshots, _clock)
            .ExecuteAsync(new(Plan, "C:\\library", "C:\\backup", "test", true, true), null,
                CancellationToken.None);

        private static CalibreCommandResult Command(string kind) => new(
            kind, true, 0, [], string.Empty, string.Empty, TimeSpan.FromMilliseconds(1));
    }

    private static (ExactBinaryCleanupPlan Plan, LibrarySnapshot Before) ApprovedPlan()
    {
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('a', 64)));
        CalibreBook keeper = Book(1, "Keeper", "Alice", fingerprint);
        CalibreBook duplicate = Book(2, "Duplicate metadata", "Bob", fingerprint);
        CalibreBook unrelated = Book(3, "Unrelated", "Carol",
            new(8, new Sha256Digest(new string('b', 64))));
        CalibreBook[] beforeBooks = [keeper, duplicate, unrelated];
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library");
        LibrarySnapshot before = new(identity, Now, beforeBooks, [], ExactBinaryDuplicateDetector.Detect(beforeBooks));
        ExactBinaryDuplicateGroup group = before.ExactBinaryDuplicateGroups.Single();
        ICleanupPlanIdGenerator planIds = A.Fake<ICleanupPlanIdGenerator>();
        A.CallTo(() => planIds.Create()).Returns(new CleanupPlanId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        ExactBinaryCleanupPlan valid = new GenerateExactBinaryCleanupPlanUseCase(planIds, clock).Execute(
            before, group.Id).Plan!;
        ExactBinaryCleanupPlan approved = new ApproveExactBinaryCleanupPlanUseCase(clock).Execute(valid, before).Plan!;
        return (approved, before);
    }

    private static CalibreBook Book(long id, string title, string author, FormatFileFingerprint fingerprint)
    {
        string directory = $"{author}/{title} ({id})";
        BookFormat format = new("EPUB", "book", $"{directory}/book.epub", FormatFileStatus.Present,
            fingerprint, new(fingerprint.SizeInBytes, Now, Now, 0));
        return new(new(id), title, author, [new(new(id), author, author)], [], [format], directory);
    }
}
