using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Application.Tests.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Recoveries;

public sealed class ExecuteApprovedRecoveryPlanUseCaseTests
{
    [Fact]
    public async Task VerifiedBackupAndConstructiveRecoveryAlwaysPrecedeDestructiveRecovery()
    {
        Harness harness = await Harness.CreateAsync(RecoveryCommandBehavior.Success);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.Recovered,
            string.Join(" | ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}")));
        result.SemanticPreStateRestored.Should().BeTrue();
        result.RecordIdMappings.Should().ContainSingle().Which.Should().Match<RecoveryRecordIdMapping>(
            value => value.OriginalRecordId == new CalibreBookId(2)
                && value.CleanupTargetRecordId == new CalibreBookId(1)
                && value.RecoveredRecordId == new CalibreBookId(42)
                && value.RestoredFormats.Contains("PDF"));
        int backup = harness.Trace.IndexOf("backup:verified");
        int firstConstructive = harness.Trace.FindIndex(value =>
            value.StartsWith("construct:", StringComparison.Ordinal));
        int lastConstructive = harness.Trace.FindLastIndex(value =>
            value.StartsWith("construct:", StringComparison.Ordinal));
        int firstDestructive = harness.Trace.FindIndex(value =>
            value.StartsWith("destroy:", StringComparison.Ordinal));
        backup.Should().BeLessThan(firstConstructive);
        lastConstructive.Should().BeLessThan(firstDestructive);
        harness.Journal.Events.Should().Contain(value =>
            value.Kind == "CurrentStateBackupVerified");
        harness.Journal.Events.Should().Contain(value =>
            value.Kind == "DestructiveRecoveryStarting");
        RecoveryRecordIdMapping finalizedMapping = harness.Journal.Events
            .Should().ContainSingle(value =>
                value.Kind == "RecordIdMappingFinalized").Which
            .RecordIdMapping!;
        finalizedMapping.OriginalRecordId.Should().Be(new CalibreBookId(2));
        finalizedMapping.RecoveredRecordId.Should().Be(new CalibreBookId(42));
        finalizedMapping.RestoredFormats.Should().Contain("PDF");
        finalizedMapping.Identifiers.Should().Contain("isbn:9780306406157");
        harness.Journal.FinalEntryHash.Should().NotBeNull();
    }

    [Fact]
    public async Task CancellationDuringCurrentStateBackupPreventsEveryMutation()
    {
        Harness harness = await Harness.CreateAsync(RecoveryCommandBehavior.CancelDuringBackup);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.CancelledBeforeMutation);
        result.MutationStarted.Should().BeFalse();
        harness.Trace.Should().NotContain(value =>
            value.StartsWith("construct:", StringComparison.Ordinal)
            || value.StartsWith("destroy:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessSuccessWithoutCreatedIdMarksStateUncertainAndStops()
    {
        Harness harness = await Harness.CreateAsync(RecoveryCommandBehavior.SuccessWithoutEffect);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.PartiallyRecovered,
            string.Join(" | ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}")));
        result.SemanticPreStateRestored.Should().BeFalse();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.DELTA_COMMIT_FAILED");
        harness.StateSession.GetCurrent(harness.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
        harness.Trace.Count(value => value == "construct:create").Should().Be(1);
        harness.Trace.Should().NotContain(value =>
            value.StartsWith("destroy:", StringComparison.Ordinal));
        harness.Journal.Events.Should().ContainSingle(value =>
            value.Kind == "RecoveryStopped"
            && value.OperationId != null
            && value.OperationId.Value.StartsWith(
                "construct:create:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task DestructiveCommandFailureStopsRescansAndRequiresManualIntervention()
    {
        Harness harness = await Harness.CreateAsync(RecoveryCommandBehavior.DestructiveFailure);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.ManualInterventionRequired);
        result.FailureClassification.Should().Be(
            RecoveryFailureClassification.DestructiveCommand,
            string.Join(" | ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}")));
        harness.Trace.Count(value => value == "destroy:format").Should().Be(1);
        harness.StateSession.GetCurrent(harness.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.DESTRUCTIVE_COMMAND_FAILED");
    }

    [Fact]
    public async Task OperationVerificationJournalFailureCannotAdvanceOrReachDestruction()
    {
        Harness harness = await Harness.CreateAsync(
            RecoveryCommandBehavior.OperationVerificationJournalFailure);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.ManualInterventionRequired);
        result.FailureClassification.Should().Be(
            RecoveryFailureClassification.CrashOrIndeterminate);
        harness.Trace.Should().NotContain(value =>
            value.StartsWith("destroy:", StringComparison.Ordinal));
        harness.Journal.Events.Should().NotContain(value =>
            value.Kind == "OperationVerified");
        A.CallTo(() => harness.Resolutions.RecordResolutionAsync(
            A<RecoveryResolutionEntry>._, A<string>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TerminalSummaryFailureCannotBeReportedOrIndexedAsRecovered()
    {
        Harness harness = await Harness.CreateAsync(
            RecoveryCommandBehavior.TerminalPersistenceFailure);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.ManualInterventionRequired);
        result.FailureClassification.Should().Be(
            RecoveryFailureClassification.JournalOrStorage);
        result.SemanticPreStateRestored.Should().BeTrue(
            "the semantic scan passed even though durable success proof did not");
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.TERMINAL_SUMMARY_WRITE_FAILED");
        A.CallTo(() => harness.Resolutions.RecordResolutionAsync(
            A<RecoveryResolutionEntry>._, A<string>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExternalCollateralChangesAreIgnoredUntilExplicitRescan()
    {
        Harness harness = await Harness.CreateAsync(
            RecoveryCommandBehavior.CollateralMetadataMutation);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.Recovered);
    }

    private enum RecoveryCommandBehavior
    {
        Success,
        CancelDuringBackup,
        SuccessWithoutEffect,
        DestructiveFailure,
        OperationVerificationJournalFailure,
        TerminalPersistenceFailure,
        CollateralMetadataMutation,
    }

    private sealed class Harness
    {
        private const string Destination = "C:\\synthetic\\recovery-backup";
        private readonly RecoveryPlan _plan;
        private readonly RecoverySourceInspection _source;
        private readonly RecoveryCurrentStateSnapshot _initial;
        private readonly RecoveryCapabilityProfile _profile;
        private readonly CalibreToolDescriptor _tool;
        private readonly ExecuteApprovedRecoveryPlanUseCase _sut;

        private Harness(
            RecoveryCommandBehavior behavior,
            LibraryStateSession stateSession)
        {
            (_source, _initial, _profile, _tool) = RecoveryTestData.SourceAndCurrent();
            RecoveryPlan valid = RecoveryTestData.PlanFor(
                _source, _initial, _profile);
            _plan = RecoveryPlanLifecyclePolicy.Approve(
                valid, valid.Definition.RequiredWarningCodes,
                ExecutionTestData.Now.AddMinutes(1));
            Trace = [];
            StateSession = stateSession;
            MutableRecoveryGateway gateway = new(behavior, Trace);
            Journal = new RecordingRecoveryJournal(behavior);

            IRecoverySourceArtifactReader sourceReader =
                A.Fake<IRecoverySourceArtifactReader>();
            A.CallTo(() => sourceReader.ReadAndVerifyAsync(
                    A<string>._, A<CancellationToken>._))
                .Returns(_source);
            IRecoveryStateBackupService backup = new RecordingBackupService(
                behavior == RecoveryCommandBehavior.CancelDuringBackup, Trace);
            IRecoveryJournalStore journals = A.Fake<IRecoveryJournalStore>();
            A.CallTo(() => journals.CreateAsync(
                    A<RecoveryJournalCreateRequest>._, A<CancellationToken>._))
                .Returns(Journal);
            A.CallTo(() => journals.ReconcileAsync(
                    A<string>._, A<string>._, A<CancellationToken>._))
                .Returns(new RecoveryJournalReconciliationResult(false, []));
            IRecoveryHistoryStore history = A.Fake<IRecoveryHistoryStore>();
            A.CallTo(() => history.HasUnresolvedRecoveryAsync(
                    A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
                .Returns(false);
            Resolutions = A.Fake<IRecoveryResolutionStore>();
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            A.CallTo(() => lease.TryAcquireAsync(
                    A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(
                    new HeldRecoveryLease(), []));
            ICalibreToolDiscovery discovery = A.Fake<ICalibreToolDiscovery>();
            A.CallTo(() => discovery.DiscoverAndProbeAsync(
                    A<string>._, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(_tool, []));
            ICalibreExecutionProfileProvider profiles =
                A.Fake<ICalibreExecutionProfileProvider>();
            A.CallTo(() => profiles.EvaluateRecoveryProfile(_tool))
                .Returns(_profile);
            IRecoveryIdGenerator ids = A.Fake<IRecoveryIdGenerator>();
            A.CallTo(() => ids.CreateExecutionId()).Returns(
                new RecoveryExecutionId(Guid.Parse(
                    "dddddddd-1111-2222-3333-eeeeeeeeeeee")));
            IDestructiveRecoveryConfirmation destructive =
                A.Fake<IDestructiveRecoveryConfirmation>();
            A.CallTo(() => destructive.ConfirmAsync(
                    A<DestructiveRecoveryConfirmationRequest>._,
                    A<CancellationToken>._)).Returns(true);
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(ExecutionTestData.Now);

            _sut = new(sourceReader, StateSession, new CurrentStateReconciler(),
                backup, journals, history, Resolutions, lease, discovery,
                profiles, gateway, new RecoveryStateVerifier(), ids,
                destructive, clock);
        }

        public List<string> Trace { get; }
        public LibraryStateSession StateSession { get; }
        public string LibraryRoot => _initial.Snapshot.Identity.LibraryRoot;
        public RecordingRecoveryJournal Journal { get; }
        public IRecoveryResolutionStore Resolutions { get; }

        public static async Task<Harness> CreateAsync(RecoveryCommandBehavior behavior)
        {
            (_, RecoveryCurrentStateSnapshot initial, _, _) = RecoveryTestData.SourceAndCurrent();
            LibraryStateSession stateSession = new();
            await stateSession.StartFromScanAsync(initial.Snapshot, CancellationToken.None);
            return new(behavior, stateSession);
        }

        public Task<RecoveryExecutionResult> ExecuteAsync()
        {
            RecoveryExecutionConfirmation confirmation = new(_plan.Id,
                _plan.ArtifactRevision, _plan.ContentDigest,
                _plan.Definition.InputIdentity.SourceExecutionId,
                _plan.Definition.InputIdentity.CurrentLibraryUuid,
                _plan.Definition.InputIdentity.CanonicalRootIdentityDigest,
                _plan.Definition.InputIdentity.FullStateFingerprint,
                _plan.Definition.InputIdentity.RecoveryCapabilityProfile,
                Destination,
                ExecuteApprovedRecoveryPlanUseCase.ComputeDestructiveDigest(
                    _plan.Definition.OperationGraph),
                ExecutionTestData.Now, true, true);
            return _sut.ExecuteAsync(new(_plan, _source,
                _initial.Snapshot.Identity.LibraryRoot, Destination, _tool,
                _profile, confirmation, "1.0.0"), null, CancellationToken.None);
        }
    }

    private sealed class MutableRecoveryGateway(
        RecoveryCommandBehavior behavior,
        List<string> trace) : IRecoveryCalibreGateway
    {
        private static RecoveryCalibreCommandResult Success(
            string kind,
            CalibreBookId? created = null) =>
            new(kind, true, 0, [], string.Empty, string.Empty,
                TimeSpan.Zero, created);

        public Task<RecoveryCalibreCommandResult> CreateEmptyRecordAsync(
            CreateRecoveryRecordCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("construct:create");
            CalibreBookId createdId = new(42);
            return Task.FromResult(Success("add",
                behavior == RecoveryCommandBehavior.SuccessWithoutEffect ? null : createdId));
        }

        public Task<RecoveryCalibreCommandResult> SetMetadataFieldAsync(
            SetRecoveryMetadataFieldCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add($"construct:metadata:{request.Field}");
            return Task.FromResult(Success("set_metadata"));
        }

        public Task<RecoveryCalibreCommandResult> AddOrReplaceFormatAsync(
            RestoreRecoveryFormatCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("construct:format");
            return Task.FromResult(Success("add_format"));
        }

        public Task<RecoveryCalibreCommandResult> RemoveFormatAsync(
            RemoveRecoveryFormatCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("destroy:format");
            if (behavior == RecoveryCommandBehavior.DestructiveFailure)
                return Task.FromResult(new RecoveryCalibreCommandResult(
                    "remove_format", true, 9, [], string.Empty, "failed",
                    TimeSpan.Zero, FailureCode: "CONTROLLED"));
            return Task.FromResult(Success("remove_format"));
        }

        public Task<RecoveryCalibreCommandResult> RemoveRecordAsync(
            RemoveRecoveryRecordCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("destroy:record");
            return Task.FromResult(Success("remove"));
        }
    }

    private sealed class RecordingBackupService(
        bool cancel,
        List<string> trace) : IRecoveryStateBackupService
    {
        public Task<RecoveryBackupDestinationValidation> ValidateDestinationAsync(
            string libraryRoot,
            string destination,
            long requiredBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RecoveryBackupDestinationValidation(
                destination, long.MaxValue, []));

        public Task<string> CreateWorkspaceAsync(
            RecoveryExecutionId recoveryExecutionId,
            string canonicalDestinationIdentity,
            CancellationToken cancellationToken) =>
            Task.FromResult($"{canonicalDestinationIdentity}\\recovery-{recoveryExecutionId}");

        public Task<RecoveryCurrentStateBackupResult> CreateAndVerifyAsync(
            CreateRecoveryCurrentStateBackupRequest request,
            CancellationToken cancellationToken)
        {
            trace.Add("backup:started");
            if (cancel) throw new OperationCanceledException(cancellationToken);
            trace.Add("backup:verified");
            RecoveryCurrentStateBackup backup = new(request.RecoveryExecutionId,
                request.Plan.Id, request.Plan.ContentDigest,
                request.CurrentState.Snapshot.Identity.CalibreLibraryUuid,
                request.BundleIdentity, $"{request.BundleIdentity}\\manifest.json",
                new(new string('a', 64)), new(new string('b', 64)),
                request.CurrentState.FullFingerprint, [], new Dictionary<
                    (CalibreBookId RecordId, string Format), string>());
            return Task.FromResult(new RecoveryCurrentStateBackupResult(backup, []));
        }

        public Task<IReadOnlyList<RecoveryIssue>> VerifyAvailableAsync(
            RecoveryCurrentStateBackup backup,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecoveryIssue>>([]);
    }

    private sealed class RecordingRecoveryJournal(
        RecoveryCommandBehavior behavior) : IRecoveryJournalSession
    {
        public List<RecoveryJournalEvent> Events { get; } = [];
        public string JournalIdentity => "C:\\synthetic\\recovery.journal.jsonl";
        public string? FinalEntryHash { get; private set; }
        public bool IsAvailable => true;

        public Task AppendAsync(
            RecoveryJournalEvent value,
            CancellationToken cancellationToken)
        {
            if (behavior
                    == RecoveryCommandBehavior.OperationVerificationJournalFailure
                && value.Kind == "OperationVerified")
                throw new IOException(
                    "Controlled operation-verification journal failure.");
            Events.Add(value);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            RecoveryHistoryEntry value,
            CancellationToken cancellationToken)
        {
            if (behavior == RecoveryCommandBehavior.TerminalPersistenceFailure)
                throw new IOException(
                    "Controlled terminal journal persistence failure.");
            FinalEntryHash = new string('f', 64);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HeldRecoveryLease : ILibraryMutationLeaseHandle
    {
        public string LeaseIdentity => "held";
        public bool IsHeld => true;
        public LibraryMutationKind MutationKind => LibraryMutationKind.Recovery;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
