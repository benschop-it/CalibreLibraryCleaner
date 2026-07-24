using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteApprovedCleanupPlanUseCaseTests
{
    [Fact]
    public async Task VerifiedBackupConstructiveVerificationAndDestructiveGatePrecedeRecordRemoval()
    {
        Harness harness = Harness.Success();

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.IsCompleted.Should().BeTrue();
        harness.Trace.Should().ContainInOrder("lease", "scan", "backup-inputs", "export-1", "export-2",
            "backup-sealed", "scan", "recovery-guard", "mutation-marker", "add", "scan",
            "destructive-confirmation", "remove", "scan", "scan");
        harness.Trace.IndexOf("remove").Should().BeGreaterThan(harness.Trace.IndexOf("destructive-confirmation"));
        A.CallTo(() => harness.Backup.VerifyAvailableAsync(A<ExecutionWorkspace>._, A<VerifiedBackupManifest>._, A<CancellationToken>._))
            .MustHaveHappened(3, Times.OrMore);
        A.CallTo(() => harness.History.RecordAsync(A<ExecutionHistoryEntry>.That.Matches(value =>
            value.Disposition == CleanupExecutionDisposition.Completed), A<string>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BackupFailurePreventsEveryMutatingCommand()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.Backup.VerifyAndSealAsync(A<SealBackupRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new ExecutionBackupResult(null, harness.RawPaths,
                [new("EXECUTION.BACKUP_HASH_MISMATCH", ExecutionIssueSeverity.BlockingError, "Backup mismatch.")])));

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(CleanupExecutionState.BackupFailed);
        result.MutationStarted.Should().BeFalse();
        A.CallTo(() => harness.Commands.AddOrReplaceFormatAsync(A<AddOrReplaceCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ChangedSecondScanStopsBeforeMutationEvenAfterBackupWasVerified()
    {
        Harness harness = Harness.Success();
        harness.SetScans(harness.Preflight, ExecutionTestData.ChangedSource(harness.Preflight));

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(CleanupExecutionState.ExecutionFailedBeforeMutation);
        result.MutationStarted.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.PLAN_STALE" || value.Code == "EXECUTION.FINAL_GATE_CHANGED");
        A.CallTo(() => harness.Commands.AddOrReplaceFormatAsync(A<AddOrReplaceCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ConstructiveVerificationFailureBlocksDestructionAndRequiresRecovery()
    {
        Harness harness = Harness.Success();
        harness.SetScans(harness.Preflight, harness.Preflight, harness.Preflight, harness.Preflight);

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.Disposition.Should().Be(CleanupExecutionDisposition.RecoveryRequired);
        result.MutationStarted.Should().BeTrue();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.RETAINED_FORMAT_MISSING");
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CancellationDuringConstructiveCommandWaitsForVerificationAndStopsBeforeRemoval()
    {
        Harness harness = Harness.Success();
        CancellationTokenSource cancellation = new();
        A.CallTo(() => harness.Commands.AddOrReplaceFormatAsync(A<AddOrReplaceCalibreFormatRequest>._, A<CancellationToken>._))
            .Invokes(() => cancellation.Cancel())
            .Returns(Task.FromResult(Harness.Command("add_format")));

        CleanupExecutionResult result = await harness.ExecuteAsync(cancellation.Token);

        result.Disposition.Should().Be(CleanupExecutionDisposition.RecoveryRequired);
        harness.Trace.Should().Contain("scan");
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Journal.AppendAsync(A<ExecutionJournalEvent>.That.Matches(value =>
            value.Kind == "CancellationRequested"), A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task DestructiveCommandFailureStopsLaterWorkAndPersistsRecoveryRequired()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new CalibreCommandResult("remove", true, 1, [], string.Empty,
                "controlled failure", TimeSpan.FromMilliseconds(1), "CALIBRE_PROCESS_NONZERO_EXIT")));

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.Disposition.Should().Be(CleanupExecutionDisposition.RecoveryRequired);
        result.FailureClassification.Should().Be(CleanupExecutionFailureClassification.DestructiveCommand);
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.DESTRUCTIVE_COMMAND_FAILED");
    }

    [Fact]
    public async Task UnapprovedPlanIsRejectedBeforeLeaseAndBackup()
    {
        (CleanupPlan approved, LibrarySnapshot preflight, _, _) = ExecutionTestData.Approved();
        CleanupPlan valid = new(approved.Id, approved.SchemaVersion, approved.PolicyVersion,
            new(1), CleanupPlanState.Valid, approved.ContentDigest, approved.InputIdentity,
            approved.CreatedAtUtc, approved.LastValidatedAtUtc, approved.Definition, approved.Validation,
            null, null, approved.LifecycleHistory.Take(1));
        Harness harness = Harness.Success(valid, preflight);

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(CleanupExecutionState.PreflightFailed);
        A.CallTo(() => harness.Lease.TryAcquireAsync(A<ExecutionLeaseRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task LeaseContentionFailsBeforeWorkspaceBackupScanOrMutation()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.Lease.TryAcquireAsync(A<ExecutionLeaseRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new ExecutionLeaseAcquisition(null,
                [new("EXECUTION.LEASE_HELD", ExecutionIssueSeverity.BlockingError, "Held.")])));

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(CleanupExecutionState.PreflightFailed);
        result.MutationStarted.Should().BeFalse();
        A.CallTo(() => harness.Scanner.ScanFreshAsync(A<string>._, A<IProgress<LibraryScanProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Backup.CreateWorkspaceAsync(A<CleanupExecutionId>._, A<string>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PriorRecoveryRequiredBlocksBeforeBackupAndMutation()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.History.HasRecoveryRequiredAsync(
            A<string>._, A<string>._, A<CancellationToken>._)).Returns(true);

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.Issues.Should().Contain(value => value.Code == "EXECUTION.PRIOR_RECOVERY_REQUIRED");
        result.MutationStarted.Should().BeFalse();
        A.CallTo(() => harness.Backup.CreateInputsAsync(A<CreateBackupInputsRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ChangedToolAtPerCommandGateStopsBeforeMutation()
    {
        Harness harness = Harness.Success();
        CalibreToolDescriptor changed = new(harness.Tool.CanonicalExecutablePath,
            new(harness.Tool.CanonicalExecutablePath, "9.11.0", new(new string('f', 64)),
                "calibredb/windows/9.11.0"), harness.Tool.Capabilities);
        A.CallTo(() => harness.Tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new CalibreToolDiscoveryResult(harness.Tool, []),
                new CalibreToolDiscoveryResult(harness.Tool, []),
                new CalibreToolDiscoveryResult(changed, []));

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(CleanupExecutionState.ExecutionFailedBeforeMutation);
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.TOOL_CHANGED_AT_GATE");
        A.CallTo(() => harness.Commands.AddOrReplaceFormatAsync(A<AddOrReplaceCalibreFormatRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task FreshStateChangeAtPerCommandGateStopsBeforeMutation()
    {
        Harness harness = Harness.Success();
        harness.SetScans(
            harness.Preflight,
            harness.Preflight,
            ExecutionTestData.ChangedSource(harness.Preflight));

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(CleanupExecutionState.ExecutionFailedBeforeMutation);
        result.MutationStarted.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.SOURCE_STATE_CHANGED");
        A.CallTo(() => harness.Commands.AddOrReplaceFormatAsync(
            A<AddOrReplaceCalibreFormatRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task JournalFailureAfterMutationProducesDurableRecoveryClassificationWithoutLaterRemoval()
    {
        Harness harness = Harness.Success();
        A.CallTo(() => harness.Journal.AppendAsync(
                A<ExecutionJournalEvent>.That.Matches(value => value.Kind == "OperationVerified"),
                A<CancellationToken>._))
            .ThrowsAsync(new ExecutionJournalPersistenceException("controlled journal failure",
                new IOException("controlled storage failure")));

        CleanupExecutionResult result = await harness.ExecuteAsync();

        result.Disposition.Should().Be(CleanupExecutionDisposition.RecoveryRequired);
        result.FailureClassification.Should().Be(CleanupExecutionFailureClassification.Journal);
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.JOURNAL_WRITE_FAILED");
        A.CallTo(() => harness.Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    private sealed class Harness
    {
        private readonly ExecuteApprovedCleanupPlanUseCase _useCase;
        private readonly CleanupExecutionConfirmation _confirmation;
        private Queue<LibrarySnapshot> _scans;

        private Harness(
            CleanupPlan plan,
            LibrarySnapshot preflight,
            LibrarySnapshot constructive,
            LibrarySnapshot final)
        {
            Plan = plan;
            Preflight = preflight;
            Trace = [];
            Scanner = A.Fake<IExecutionLibraryScanner>();
            Tools = A.Fake<ICalibreToolDiscovery>();
            Commands = A.Fake<ICalibreCommandGateway>();
            Lease = A.Fake<ICleanupExecutionLease>();
            Backup = A.Fake<IExecutionBackupStore>();
            Journals = A.Fake<IExecutionJournalStore>();
            Journal = A.Fake<IExecutionJournalSession>();
            History = A.Fake<IExecutionHistoryStore>();
            ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
            IDestructiveExecutionConfirmation destructive = A.Fake<IDestructiveExecutionConfirmation>();
            IClock clock = A.Fake<IClock>();
            ICleanupExecutionLeaseHandle handle = A.Fake<ICleanupExecutionLeaseHandle>();
            Tool = new("C:\\trusted\\calibredb.exe", new("C:\\trusted\\calibredb.exe", "9.11.0",
                new(new string('b', 64)), "calibredb/windows/9.11.0"), Enum.GetValues<CalibreExecutionCapability>());
            CleanupExecutionId executionId = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            Workspace = new(executionId, "C:\\backup\\execution-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "C:\\backup");
            RawPaths = plan.Definition.ExpectedLibraryState.Records.SelectMany(value => value.Formats)
                .ToDictionary(value => new BackupFormatKey(value.RecordId, value.Format, value.RelativePath),
                    value => $"C:\\backup\\raw\\{value.RecordId.Value}\\book.{value.Format.ToLowerInvariant()}");
            BackupManifestEntry[] manifestEntries = plan.Definition.BackupRequirements
                .Where(value => value.Kind != BackupRequirementKind.ExecutionAudit)
                .Select((value, index) => new BackupManifestEntry($"artifact/{index:D3}", BackupArtifactKind.PreflightEvidence,
                    1, new(new string((char)('a' + index % 6), 64)), [value.Id], value.RecordId, value.Format)).ToArray();
            Manifest = VerifiedBackupManifest.Create(executionId, plan, ExecutionTestData.Now, manifestEntries);
            ExecutionBackupInputs inputs = new(Workspace,
                plan.Definition.InvolvedRecordIds.ToDictionary(value => value, value => $"C:\\backup\\exports\\{value.Value}"), RawPaths, []);
            _scans = new([
                preflight,
                preflight,
                preflight,
                constructive,
                constructive,
                constructive,
                final,
                final,
            ]);
            A.CallTo(() => Scanner.ScanFreshAsync(A<string>._, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
                .Invokes(() => Trace.Add("scan"))
                .ReturnsLazily(() => Task.FromResult(LibraryScanOutcome.Success(_scans.Dequeue())));
            A.CallTo(() => Tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new CalibreToolDiscoveryResult(Tool, [])));
            A.CallTo(() => Lease.TryAcquireAsync(A<ExecutionLeaseRequest>._, A<CancellationToken>._))
                .Invokes(() => Trace.Add("lease"))
                .Returns(Task.FromResult(new ExecutionLeaseAcquisition(handle, [])));
            A.CallTo(() => handle.IsHeld).Returns(true);
            A.CallTo(() => Backup.ValidateDestinationAsync(A<string>._, A<string>._, A<long>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new BackupDestinationValidation("C:\\backup", long.MaxValue, [])));
            A.CallTo(() => Backup.CreateWorkspaceAsync(A<CleanupExecutionId>._, A<string>._, A<CancellationToken>._))
                .Returns(Task.FromResult(Workspace));
            A.CallTo(() => Backup.CreateInputsAsync(A<CreateBackupInputsRequest>._, A<CancellationToken>._))
                .Invokes(() => Trace.Add("backup-inputs")).Returns(Task.FromResult(inputs));
            A.CallTo(() => Backup.VerifyAndSealAsync(A<SealBackupRequest>._, A<CancellationToken>._))
                .Invokes(() => Trace.Add("backup-sealed"))
                .Returns(Task.FromResult(new ExecutionBackupResult(Manifest, RawPaths, [])));
            A.CallTo(() => Backup.VerifyAvailableAsync(A<ExecutionWorkspace>._, A<VerifiedBackupManifest>._, A<CancellationToken>._))
                .Returns(Task.FromResult<IReadOnlyList<ExecutionIssue>>([]));
            A.CallTo(() => Journals.ReconcileAsync(A<string>._, A<string>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new JournalReconciliationResult(false, [])));
            A.CallTo(() => Journals.CreateAsync(A<ExecutionJournalCreateRequest>._, A<CancellationToken>._))
                .Returns(Task.FromResult(Journal));
            A.CallTo(() => Journal.JournalIdentity).Returns("C:\\backup\\execution.journal.jsonl");
            A.CallTo(() => Journal.IsAvailable).Returns(true);
            A.CallTo(() => Journal.AppendAsync(A<ExecutionJournalEvent>._, A<CancellationToken>._))
                .Invokes(call =>
                {
                    ExecutionJournalEvent value = call.GetArgument<ExecutionJournalEvent>(0)!;
                    if (value.Kind == "MutationStarting") Trace.Add("mutation-marker");
                }).Returns(Task.CompletedTask);
            A.CallTo(() => Journal.CompleteAsync(A<ExecutionHistoryEntry>._, A<CancellationToken>._)).Returns(Task.CompletedTask);
            A.CallTo(() => History.HasRecoveryRequiredAsync(
                A<string>._, A<string>._, A<CancellationToken>._)).Returns(false);
            A.CallTo(() => History.RecordAsync(
                    A<ExecutionHistoryEntry>._, A<string>._, A<CancellationToken>._))
                .Invokes(call =>
                {
                    ExecutionHistoryEntry value = call.GetArgument<ExecutionHistoryEntry>(0)!;
                    if (value.Disposition == CleanupExecutionDisposition.RecoveryRequired
                        && value.FailureClassification == CleanupExecutionFailureClassification.CrashOrIndeterminate)
                        Trace.Add("recovery-guard");
                })
                .Returns(Task.CompletedTask);
            A.CallTo(() => Commands.ExportRecordAsync(A<ExportCalibreRecordRequest>._, A<CancellationToken>._))
                .Invokes(call => Trace.Add($"export-{call.GetArgument<ExportCalibreRecordRequest>(0)!.RecordId.Value}"))
                .Returns(Task.FromResult(Command("export")));
            A.CallTo(() => Commands.AddOrReplaceFormatAsync(A<AddOrReplaceCalibreFormatRequest>._, A<CancellationToken>._))
                .Invokes(() => Trace.Add("add")).Returns(Task.FromResult(Command("add_format")));
            A.CallTo(() => Commands.RemoveRecordAsync(A<RemoveCalibreRecordRequest>._, A<CancellationToken>._))
                .Invokes(() => Trace.Add("remove")).Returns(Task.FromResult(Command("remove")));
            A.CallTo(() => ids.Create()).Returns(executionId);
            A.CallTo(() => destructive.ConfirmAsync(A<DestructiveExecutionConfirmationRequest>._, A<CancellationToken>._))
                .Invokes(() => Trace.Add("destructive-confirmation")).Returns(true);
            A.CallTo(() => clock.GetUtcNow()).Returns(ExecutionTestData.Now);
            Sha256Digest operationGraphDigest =
                CleanupExecutionCapabilityPolicy.Evaluate(plan).Graph?.Digest
                ?? new Sha256Digest(new string('d', 64));
            _confirmation = new(plan.Id, plan.ArtifactRevision, plan.ContentDigest, plan.InputIdentity.LibraryUuid,
                preflight.Identity.LibraryRoot, operationGraphDigest,
                Tool.Identity, "C:\\backup", ExecutionTestData.Now, true, true);
            _useCase = new(Scanner, Tools, Commands, Lease, Backup, Journals, History, ids, destructive, clock);
        }

        public CleanupPlan Plan { get; }
        public LibrarySnapshot Preflight { get; }
        public List<string> Trace { get; }
        public IExecutionLibraryScanner Scanner { get; }
        public ICalibreToolDiscovery Tools { get; }
        public ICalibreCommandGateway Commands { get; }
        public ICleanupExecutionLease Lease { get; }
        public IExecutionBackupStore Backup { get; }
        public IExecutionJournalStore Journals { get; }
        public IExecutionJournalSession Journal { get; }
        public IExecutionHistoryStore History { get; }
        public CalibreToolDescriptor Tool { get; }
        public ExecutionWorkspace Workspace { get; }
        public IReadOnlyDictionary<BackupFormatKey, string> RawPaths { get; }
        public VerifiedBackupManifest Manifest { get; }

        public static Harness Success(CleanupPlan? plan = null, LibrarySnapshot? preflight = null)
        {
            (CleanupPlan defaultPlan, LibrarySnapshot defaultPreflight, LibrarySnapshot constructive, LibrarySnapshot final) = ExecutionTestData.Approved();
            return new(plan ?? defaultPlan, preflight ?? defaultPreflight, constructive, final);
        }

        public void SetScans(params LibrarySnapshot[] scans) => _scans = new(scans);

        public Task<CleanupExecutionResult> ExecuteAsync(CancellationToken cancellationToken = default) =>
            _useCase.ExecuteAsync(new(Plan, Preflight.Identity.LibraryRoot, "C:\\backup", _confirmation, "1.0.0"),
                null, cancellationToken);

        public static CalibreCommandResult Command(string kind) => new(kind, true, 0, [], string.Empty,
            string.Empty, TimeSpan.FromMilliseconds(1));
    }
}
