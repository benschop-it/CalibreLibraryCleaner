using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public enum RecoveryExecutionState
{
    Created,
    PreflightValidating,
    BackingUpCurrentState,
    CurrentStateBackupVerified,
    RestoringConstructiveState,
    VerifyingConstructiveState,
    ReadyForDestructiveRecovery,
    ApplyingDestructiveRecovery,
    FinalVerifying,
    Recovered,
    PartiallyRecovered,
    VerificationFailed,
    CancelledBeforeMutation,
    RecoveryFailed,
    ManualInterventionRequired,
    Revoked,
    Stale,
}

public enum RecoveryFailureClassification
{
    None,
    Eligibility,
    Preflight,
    CurrentStateBackup,
    ConstructiveCommand,
    ConstructiveVerification,
    DestructiveCommand,
    FinalVerification,
    JournalOrStorage,
    Cancellation,
    CrashOrIndeterminate,
}

public enum RecoveryOperationStatus
{
    Planned,
    Starting,
    CommandSucceeded,
    Verified,
    SatisfiedNoAction,
    Failed,
    NotStarted,
}

public sealed record RecoveryOperationProgress(
    RecoveryOperation Operation,
    RecoveryOperationStatus Status,
    string? FailureCode = null);

public sealed record RecoveryExecution
{
    private RecoveryExecution(
        RecoveryExecutionId id,
        RecoveryPlanId planId,
        RecoveryPlanContentDigest planContentDigest,
        CleanupExecutionId sourceExecutionId,
        string libraryUuid,
        RecoveryOperationGraph graph,
        RecoveryExecutionState state,
        string? currentStateBackupManifestDigest,
        IEnumerable<RecoveryOperationProgress> operations,
        IEnumerable<RecoveryRecordIdMapping> recordIdMappings,
        bool mutationStarted,
        bool constructiveOperationCompleted,
        bool destructiveRecoveryStarted,
        string lastVerifiedSafeBoundary,
        bool semanticPreStateRestored,
        bool preservedUnexpectedContent,
        RecoveryFailureClassification failureClassification)
    {
        Id = id;
        PlanId = planId;
        PlanContentDigest = planContentDigest;
        SourceExecutionId = sourceExecutionId;
        LibraryUuid = libraryUuid;
        Graph = graph;
        State = state;
        CurrentStateBackupManifestDigest = currentStateBackupManifestDigest;
        Operations = Array.AsReadOnly(operations.ToArray());
        RecordIdMappings = Array.AsReadOnly(recordIdMappings.OrderBy(value => value.LogicalRecordId.Value, StringComparer.Ordinal).ToArray());
        MutationStarted = mutationStarted;
        ConstructiveOperationCompleted = constructiveOperationCompleted;
        DestructiveRecoveryStarted = destructiveRecoveryStarted;
        LastVerifiedSafeBoundary = lastVerifiedSafeBoundary;
        SemanticPreStateRestored = semanticPreStateRestored;
        PreservedUnexpectedContent = preservedUnexpectedContent;
        FailureClassification = failureClassification;
    }

    public RecoveryExecutionId Id { get; }
    public RecoveryPlanId PlanId { get; }
    public RecoveryPlanContentDigest PlanContentDigest { get; }
    public CleanupExecutionId SourceExecutionId { get; }
    public string LibraryUuid { get; }
    public RecoveryOperationGraph Graph { get; }
    public RecoveryExecutionState State { get; }
    public string? CurrentStateBackupManifestDigest { get; }
    public IReadOnlyList<RecoveryOperationProgress> Operations { get; }
    public IReadOnlyList<RecoveryRecordIdMapping> RecordIdMappings { get; }
    public bool MutationStarted { get; }
    public bool ConstructiveOperationCompleted { get; }
    public bool DestructiveRecoveryStarted { get; }
    public string LastVerifiedSafeBoundary { get; }
    public bool SemanticPreStateRestored { get; }
    public bool PreservedUnexpectedContent { get; }
    public bool ChangedRecordIds => RecordIdMappings.Any(value => value.NumericIdChanged);
    public RecoveryFailureClassification FailureClassification { get; }

    public static RecoveryExecution Create(RecoveryExecutionId id, RecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.State != RecoveryPlanState.Approved || plan.Approval?.ContentDigest != plan.ContentDigest)
            throw new ArgumentException("Recovery execution requires an approved immutable plan.", nameof(plan));
        return new(id, plan.Id, plan.ContentDigest,
            plan.Definition.InputIdentity.SourceExecutionId,
            plan.Definition.InputIdentity.CurrentLibraryUuid,
            plan.Definition.OperationGraph,
            RecoveryExecutionState.Created, null,
            plan.Definition.OperationGraph.Operations.Select(value => new RecoveryOperationProgress(value, RecoveryOperationStatus.Planned)),
            [], false, false, false, "Created", false,
            plan.Definition.ExpectedFinalState.PreservedContent.Count > 0, RecoveryFailureClassification.None);
    }

    public RecoveryExecution Transition(RecoveryExecutionState next)
    {
        if (!IsLegal(State, next)) throw new InvalidOperationException($"Illegal recovery transition {State} -> {next}.");
        if (next == RecoveryExecutionState.CurrentStateBackupVerified && string.IsNullOrWhiteSpace(CurrentStateBackupManifestDigest))
            throw new InvalidOperationException("Current-state backup verification requires a sealed manifest.");
        if (next == RecoveryExecutionState.ReadyForDestructiveRecovery
            && (CurrentStateBackupManifestDigest is null
                || Graph.ConstructiveOperations.Any(value => Status(value.Id) is not (RecoveryOperationStatus.Verified or RecoveryOperationStatus.SatisfiedNoAction))))
            throw new InvalidOperationException("Destructive recovery requires verified backup and constructive operations.");
        if (next == RecoveryExecutionState.Recovered
            && (!SemanticPreStateRestored
                || Operations.Any(value => value.Status is not (RecoveryOperationStatus.Verified
                    or RecoveryOperationStatus.SatisfiedNoAction))))
            throw new InvalidOperationException("Recovered requires final semantic verification and every expectation.");
        return Copy(state: next);
    }

    public RecoveryExecution AttachVerifiedCurrentStateBackup(string manifestDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDigest);
        if (State != RecoveryExecutionState.BackingUpCurrentState)
            throw new InvalidOperationException("A current-state manifest can be attached only during backup.");
        return Copy(currentStateBackupManifestDigest: manifestDigest.Trim());
    }

    public RecoveryExecution MarkMutationStarting()
    {
        if (CurrentStateBackupManifestDigest is null
            || State is not (RecoveryExecutionState.CurrentStateBackupVerified
                or RecoveryExecutionState.RestoringConstructiveState
                or RecoveryExecutionState.ReadyForDestructiveRecovery
                or RecoveryExecutionState.ApplyingDestructiveRecovery))
            throw new InvalidOperationException("Recovery mutation cannot start before verified current-state backup.");
        return Copy(mutationStarted: true);
    }

    public RecoveryExecution StartOperation(RecoveryOperationId operationId)
    {
        if (!MutationStarted || CurrentStateBackupManifestDigest is null)
            throw new InvalidOperationException("A recovery operation cannot start before the mutation boundary.");
        if (Operations.Any(value => value.Status == RecoveryOperationStatus.Starting))
            throw new InvalidOperationException("Only one recovery operation can run at a time.");
        int index = Find(operationId);
        RecoveryOperationProgress current = Operations[index];
        if (!current.Operation.IsDispatchable || current.Status != RecoveryOperationStatus.Planned
            || !DependenciesSatisfied(current.Operation))
            throw new InvalidOperationException("The recovery operation is not dependency-ready.");
        if (current.Operation.Phase == RecoveryOperationPhase.Destructive
            && State != RecoveryExecutionState.ApplyingDestructiveRecovery)
            throw new InvalidOperationException("A destructive operation cannot run before its separate gate.");
        if (current.Operation.Phase == RecoveryOperationPhase.Constructive
            && State != RecoveryExecutionState.RestoringConstructiveState)
            throw new InvalidOperationException("A constructive operation cannot run outside constructive restoration.");
        return Replace(index, current with { Status = RecoveryOperationStatus.Starting });
    }

    public RecoveryExecution MarkCommandSucceeded(RecoveryOperationId operationId)
    {
        int index = Find(operationId);
        RecoveryOperationProgress current = Operations[index];
        if (current.Status != RecoveryOperationStatus.Starting)
            throw new InvalidOperationException("Only a started recovery operation can record command success.");
        return Replace(index, current with { Status = RecoveryOperationStatus.CommandSucceeded });
    }

    public RecoveryExecution MarkOperationVerified(RecoveryOperationId operationId, string safeBoundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeBoundary);
        int index = Find(operationId);
        RecoveryOperationProgress current = Operations[index];
        if (current.Status != RecoveryOperationStatus.CommandSucceeded)
            throw new InvalidOperationException("Only a process-success operation can be semantically verified.");
        RecoveryExecution changed = Replace(index, current with { Status = RecoveryOperationStatus.Verified });
        return changed.Copy(
            constructiveOperationCompleted: ConstructiveOperationCompleted
                || current.Operation.Phase == RecoveryOperationPhase.Constructive,
            lastVerifiedSafeBoundary: safeBoundary.Trim());
    }

    public RecoveryExecution SatisfyNonMutating(RecoveryOperationId operationId)
    {
        int index = Find(operationId);
        RecoveryOperationProgress current = Operations[index];
        if (current.Operation.Phase != RecoveryOperationPhase.NonMutating
            || current.Status != RecoveryOperationStatus.Planned
            || !DependenciesSatisfied(current.Operation))
            throw new InvalidOperationException("The non-mutating recovery expectation is not ready.");
        return Replace(index, current with { Status = RecoveryOperationStatus.SatisfiedNoAction });
    }

    public RecoveryExecution BeginDestructiveRecovery()
    {
        if (State != RecoveryExecutionState.ReadyForDestructiveRecovery)
            throw new InvalidOperationException("Destructive recovery requires its verified gate.");
        return Copy(state: RecoveryExecutionState.ApplyingDestructiveRecovery,
            destructiveRecoveryStarted: true, mutationStarted: true);
    }

    public RecoveryExecution AddRecordIdMapping(RecoveryRecordIdMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (RecordIdMappings.Any(value => value.LogicalRecordId == mapping.LogicalRecordId
            || value.RecoveredRecordId == mapping.RecoveredRecordId))
            throw new InvalidOperationException("A recovery record-ID mapping is ambiguous or duplicated.");
        return Copy(recordIdMappings: RecordIdMappings.Append(mapping));
    }

    public RecoveryExecution RecordRestoredFormat(
        LogicalRecoveryRecordId logicalRecordId,
        string format)
    {
        ArgumentNullException.ThrowIfNull(logicalRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        int index = RecordIdMappings.ToList().FindIndex(value =>
            value.LogicalRecordId == logicalRecordId);
        if (index < 0)
            return this;
        RecoveryRecordIdMapping prior = RecordIdMappings[index];
        RecoveryRecordIdMapping updated = new(prior.LogicalRecordId,
            prior.OriginalRecordId, prior.CleanupTargetRecordId,
            prior.RecoveredRecordId, prior.RestoredFormats.Append(format),
            prior.Identifiers, prior.MappedAtUtc);
        RecoveryRecordIdMapping[] values = RecordIdMappings.ToArray();
        values[index] = updated;
        return Copy(recordIdMappings: values);
    }

    public RecoveryExecution FinalizeRecordIdMapping(
        LogicalRecoveryRecordId logicalRecordId,
        IEnumerable<string> restoredFormats,
        IEnumerable<string> verifiedIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(logicalRecordId);
        int index = RecordIdMappings.ToList().FindIndex(value =>
            value.LogicalRecordId == logicalRecordId);
        if (index < 0)
            throw new InvalidOperationException(
                "A recovery record-ID mapping must exist before it can be finalized.");
        RecoveryRecordIdMapping prior = RecordIdMappings[index];
        RecoveryRecordIdMapping updated = new(prior.LogicalRecordId,
            prior.OriginalRecordId, prior.CleanupTargetRecordId,
            prior.RecoveredRecordId, restoredFormats, verifiedIdentifiers,
            prior.MappedAtUtc);
        RecoveryRecordIdMapping[] values = RecordIdMappings.ToArray();
        values[index] = updated;
        return Copy(recordIdMappings: values);
    }

    public RecoveryExecution MarkFailed(
        RecoveryOperationId? operationId,
        string failureCode,
        RecoveryFailureClassification classification,
        bool manualIntervention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        RecoveryExecution changed = this;
        if (operationId is not null)
        {
            int index = Find(operationId);
            RecoveryOperationProgress current = Operations[index];
            if (current.Status is not (RecoveryOperationStatus.Starting or RecoveryOperationStatus.CommandSucceeded))
                throw new InvalidOperationException("Only a started recovery operation can fail.");
            changed = Replace(index, current with { Status = RecoveryOperationStatus.Failed, FailureCode = failureCode.Trim() });
        }
        RecoveryExecutionState terminal = manualIntervention || DestructiveRecoveryStarted
            ? RecoveryExecutionState.ManualInterventionRequired
            : MutationStarted || ConstructiveOperationCompleted
                ? RecoveryExecutionState.PartiallyRecovered
                : RecoveryExecutionState.RecoveryFailed;
        return changed.Copy(state: terminal, failureClassification: classification);
    }

    public RecoveryExecution MarkFinalVerification(bool passed, bool manualIntervention, string safeBoundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeBoundary);
        if (State != RecoveryExecutionState.FinalVerifying)
            throw new InvalidOperationException("Final recovery verification is not active.");
        if (!passed)
            return Copy(state: manualIntervention ? RecoveryExecutionState.ManualInterventionRequired
                : RecoveryExecutionState.VerificationFailed,
                lastVerifiedSafeBoundary: safeBoundary.Trim(),
                failureClassification: RecoveryFailureClassification.FinalVerification);
        RecoveryExecution verified = Copy(semanticPreStateRestored: true,
            lastVerifiedSafeBoundary: safeBoundary.Trim());
        return verified.Transition(RecoveryExecutionState.Recovered);
    }

    public static bool IsLegal(RecoveryExecutionState from, RecoveryExecutionState to) => (from, to) switch
    {
        (RecoveryExecutionState.Created, RecoveryExecutionState.PreflightValidating) => true,
        (RecoveryExecutionState.PreflightValidating, RecoveryExecutionState.BackingUpCurrentState
            or RecoveryExecutionState.CancelledBeforeMutation or RecoveryExecutionState.RecoveryFailed
            or RecoveryExecutionState.Stale or RecoveryExecutionState.Revoked) => true,
        (RecoveryExecutionState.BackingUpCurrentState, RecoveryExecutionState.CurrentStateBackupVerified
            or RecoveryExecutionState.CancelledBeforeMutation or RecoveryExecutionState.RecoveryFailed) => true,
        (RecoveryExecutionState.CurrentStateBackupVerified, RecoveryExecutionState.RestoringConstructiveState
            or RecoveryExecutionState.VerifyingConstructiveState or RecoveryExecutionState.FinalVerifying
            or RecoveryExecutionState.CancelledBeforeMutation) => true,
        (RecoveryExecutionState.RestoringConstructiveState, RecoveryExecutionState.VerifyingConstructiveState
            or RecoveryExecutionState.PartiallyRecovered or RecoveryExecutionState.ManualInterventionRequired) => true,
        (RecoveryExecutionState.RestoringConstructiveState,
            RecoveryExecutionState.CancelledBeforeMutation) => true,
        (RecoveryExecutionState.VerifyingConstructiveState, RecoveryExecutionState.ReadyForDestructiveRecovery
            or RecoveryExecutionState.FinalVerifying or RecoveryExecutionState.PartiallyRecovered
            or RecoveryExecutionState.ManualInterventionRequired
            or RecoveryExecutionState.CancelledBeforeMutation) => true,
        (RecoveryExecutionState.ReadyForDestructiveRecovery, RecoveryExecutionState.ApplyingDestructiveRecovery
            or RecoveryExecutionState.PartiallyRecovered or RecoveryExecutionState.FinalVerifying
            or RecoveryExecutionState.CancelledBeforeMutation) => true,
        (RecoveryExecutionState.ApplyingDestructiveRecovery, RecoveryExecutionState.FinalVerifying
            or RecoveryExecutionState.ManualInterventionRequired) => true,
        (RecoveryExecutionState.FinalVerifying, RecoveryExecutionState.Recovered
            or RecoveryExecutionState.VerificationFailed or RecoveryExecutionState.ManualInterventionRequired) => true,
        (RecoveryExecutionState.Recovered,
            RecoveryExecutionState.ManualInterventionRequired) => true,
        _ => false,
    };

    private RecoveryOperationStatus Status(RecoveryOperationId id) =>
        Operations.Single(value => value.Operation.Id == id).Status;

    private bool DependenciesSatisfied(RecoveryOperation operation) =>
        operation.DependencyIds.Concat(operation.PreservationDependencyIds).All(dependency =>
            Status(dependency) is RecoveryOperationStatus.Verified or RecoveryOperationStatus.SatisfiedNoAction);

    private int Find(RecoveryOperationId id)
    {
        int index = Operations.ToList().FindIndex(value => value.Operation.Id == id);
        if (index < 0) throw new ArgumentException("The operation is not in this recovery execution.", nameof(id));
        return index;
    }

    private RecoveryExecution Replace(int index, RecoveryOperationProgress progress)
    {
        RecoveryOperationProgress[] values = Operations.ToArray();
        values[index] = progress;
        return Copy(operations: values);
    }

    private RecoveryExecution Copy(
        RecoveryExecutionState? state = null,
        string? currentStateBackupManifestDigest = null,
        IEnumerable<RecoveryOperationProgress>? operations = null,
        IEnumerable<RecoveryRecordIdMapping>? recordIdMappings = null,
        bool? mutationStarted = null,
        bool? constructiveOperationCompleted = null,
        bool? destructiveRecoveryStarted = null,
        string? lastVerifiedSafeBoundary = null,
        bool? semanticPreStateRestored = null,
        bool? preservedUnexpectedContent = null,
        RecoveryFailureClassification? failureClassification = null) =>
        new(Id, PlanId, PlanContentDigest, SourceExecutionId, LibraryUuid,
            Graph, state ?? State,
            currentStateBackupManifestDigest ?? CurrentStateBackupManifestDigest,
            operations ?? Operations, recordIdMappings ?? RecordIdMappings,
            mutationStarted ?? MutationStarted,
            constructiveOperationCompleted ?? ConstructiveOperationCompleted,
            destructiveRecoveryStarted ?? DestructiveRecoveryStarted,
            lastVerifiedSafeBoundary ?? LastVerifiedSafeBoundary,
            semanticPreStateRestored ?? SemanticPreStateRestored,
            preservedUnexpectedContent ?? PreservedUnexpectedContent,
            failureClassification ?? FailureClassification);
}
