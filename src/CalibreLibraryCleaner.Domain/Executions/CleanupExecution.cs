using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Executions;

public sealed record ExecutionOperationProgress(
    CleanupExecutionOperation Operation,
    ExecutionOperationStatus Status,
    string? FailureCode = null);

public sealed record CleanupExecution
{
    private CleanupExecution(
        CleanupExecutionId id,
        CleanupPlanId planId,
        CleanupPlanContentDigest planContentDigest,
        CleanupExecutionConfirmation confirmation,
        CleanupExecutionOperationGraph graph,
        CleanupExecutionState state,
        bool mutationStarted,
        VerifiedBackupManifest? backupManifest,
        IEnumerable<ExecutionOperationProgress> operations,
        CleanupExecutionFailureClassification failureClassification,
        CleanupExecutionDisposition disposition)
    {
        Id = id;
        PlanId = planId;
        PlanContentDigest = planContentDigest;
        Confirmation = confirmation;
        Graph = graph;
        State = state;
        MutationStarted = mutationStarted;
        BackupManifest = backupManifest;
        Operations = Array.AsReadOnly(operations.ToArray());
        FailureClassification = failureClassification;
        Disposition = disposition;
    }

    public CleanupExecutionId Id { get; }
    public CleanupPlanId PlanId { get; }
    public CleanupPlanContentDigest PlanContentDigest { get; }
    public CleanupExecutionConfirmation Confirmation { get; }
    public CleanupExecutionOperationGraph Graph { get; }
    public CleanupExecutionState State { get; }
    public bool MutationStarted { get; }
    public VerifiedBackupManifest? BackupManifest { get; }
    public IReadOnlyList<ExecutionOperationProgress> Operations { get; }
    public CleanupExecutionFailureClassification FailureClassification { get; }
    public CleanupExecutionDisposition Disposition { get; }

    public static CleanupExecution Create(
        CleanupExecutionId id,
        CleanupPlan plan,
        CleanupExecutionConfirmation confirmation,
        CleanupExecutionOperationGraph graph)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(graph);
        if (confirmation.PlanId != plan.Id || confirmation.PlanRevision != plan.ArtifactRevision
            || confirmation.PlanContentDigest != plan.ContentDigest
            || confirmation.OperationGraphDigest != graph.Digest)
            throw new ArgumentException("The execution confirmation is not bound to the cleanup plan.", nameof(confirmation));
        return new(id, plan.Id, plan.ContentDigest, confirmation, graph, CleanupExecutionState.Created,
            false, null, graph.Operations.Select(value => new ExecutionOperationProgress(value, ExecutionOperationStatus.Planned)),
            CleanupExecutionFailureClassification.None, CleanupExecutionDisposition.InProgress);
    }

    public CleanupExecution Transition(CleanupExecutionState next)
    {
        if (!IsLegal(State, next)) throw new InvalidOperationException($"Illegal cleanup execution transition {State} -> {next}.");
        if (next == CleanupExecutionState.ReadyToExecute && BackupManifest is null)
            throw new InvalidOperationException("A verified backup is required before execution.");
        if (next == CleanupExecutionState.Completed && (!MutationStarted
                || Operations.Any(value => value.Status is not (ExecutionOperationStatus.Verified or ExecutionOperationStatus.SatisfiedNoOp))))
            throw new InvalidOperationException("Completion requires the mutation boundary and every operation to be verified.");
        return Copy(state: next, disposition: DispositionFor(next, MutationStarted));
    }

    public CleanupExecution AttachVerifiedBackup(VerifiedBackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (State != CleanupExecutionState.BackingUp || manifest.ExecutionId != Id
            || manifest.PlanId != PlanId || manifest.PlanContentDigest != PlanContentDigest)
            throw new InvalidOperationException("The verified backup cannot be attached in the current state.");
        return Copy(state: CleanupExecutionState.BackupVerified, backupManifest: manifest);
    }

    public CleanupExecution MarkMutationStarting()
    {
        if (State != CleanupExecutionState.ReadyToExecute || BackupManifest is null)
            throw new InvalidOperationException("Mutation cannot start before the verified execution gate.");
        return Copy(state: CleanupExecutionState.Executing, mutationStarted: true);
    }

    public CleanupExecution SatisfyNoOp(ExecutionOperationId id)
    {
        if (State is not (CleanupExecutionState.ReadyToExecute or CleanupExecutionState.Executing))
            throw new InvalidOperationException("Precondition no-ops can be satisfied only at the final execution gate.");
        int index = Find(id);
        ExecutionOperationProgress current = Operations[index];
        if (current.Status != ExecutionOperationStatus.Planned || !DependenciesSatisfied(current.Operation))
            throw new InvalidOperationException("The no-op operation is not ready.");
        return Replace(index, current with { Status = ExecutionOperationStatus.SatisfiedNoOp });
    }

    public CleanupExecution StartOperation(ExecutionOperationId id)
    {
        if (!MutationStarted || State != CleanupExecutionState.Executing || BackupManifest is null)
            throw new InvalidOperationException("A mutation operation cannot start before the mutation boundary.");
        if (Operations.Any(value => value.Status == ExecutionOperationStatus.Starting))
            throw new InvalidOperationException("Only one mutation operation may run at a time.");
        int index = Find(id);
        ExecutionOperationProgress current = Operations[index];
        if (current.Operation.Kind is not (ExecutionOperationKind.AddOrReplaceFormat or ExecutionOperationKind.RemoveRedundantRecord)
            || current.Status != ExecutionOperationStatus.Planned || !DependenciesSatisfied(current.Operation))
            throw new InvalidOperationException("The mutation operation or its dependencies are not ready.");
        return Replace(index, current with { Status = ExecutionOperationStatus.Starting });
    }

    public CleanupExecution MarkOperationSucceeded(ExecutionOperationId id)
    {
        int index = Find(id);
        ExecutionOperationProgress current = Operations[index];
        if (current.Status != ExecutionOperationStatus.Starting) throw new InvalidOperationException("Only a running operation can succeed.");
        return Replace(index, current with { Status = ExecutionOperationStatus.Succeeded });
    }

    public CleanupExecution MarkOperationVerified(ExecutionOperationId id)
    {
        int index = Find(id);
        ExecutionOperationProgress current = Operations[index];
        if (current.Status != ExecutionOperationStatus.Succeeded) throw new InvalidOperationException("Only a successful operation can be verified.");
        return Replace(index, current with { Status = ExecutionOperationStatus.Verified });
    }

    public CleanupExecution MarkOperationFailed(ExecutionOperationId id, string failureCode, CleanupExecutionFailureClassification classification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        int index = Find(id);
        ExecutionOperationProgress current = Operations[index];
        if (current.Status is not (ExecutionOperationStatus.Starting or ExecutionOperationStatus.Succeeded))
            throw new InvalidOperationException("Only a started operation can fail.");
        CleanupExecution changed = Replace(index, current with { Status = ExecutionOperationStatus.Failed, FailureCode = failureCode });
        return changed.Copy(
            state: MutationStarted ? CleanupExecutionState.ExecutionPartiallyApplied : CleanupExecutionState.ExecutionFailedBeforeMutation,
            failureClassification: classification,
            disposition: MutationStarted ? CleanupExecutionDisposition.PartiallyApplied : CleanupExecutionDisposition.Failed);
    }

    public CleanupExecution RequireRecovery(CleanupExecutionFailureClassification classification)
    {
        if (!MutationStarted) throw new InvalidOperationException("Recovery cannot be required before mutation.");
        return Copy(state: CleanupExecutionState.RecoveryRequired, failureClassification: classification,
            disposition: CleanupExecutionDisposition.RecoveryRequired);
    }

    public static bool IsLegal(CleanupExecutionState from, CleanupExecutionState to) => (from, to) switch
    {
        (CleanupExecutionState.Created, CleanupExecutionState.AcquiringLease) => true,
        (CleanupExecutionState.AcquiringLease, CleanupExecutionState.PreflightValidating or CleanupExecutionState.PreflightFailed or CleanupExecutionState.CancelledBeforeMutation) => true,
        (CleanupExecutionState.PreflightValidating, CleanupExecutionState.ReadyForBackup or CleanupExecutionState.PreflightFailed or CleanupExecutionState.CancelledBeforeMutation) => true,
        (CleanupExecutionState.ReadyForBackup, CleanupExecutionState.BackingUp or CleanupExecutionState.BackupFailed or CleanupExecutionState.CancelledBeforeMutation) => true,
        (CleanupExecutionState.BackingUp, CleanupExecutionState.BackupVerified or CleanupExecutionState.BackupFailed or CleanupExecutionState.CancelledBeforeMutation) => true,
        (CleanupExecutionState.BackupVerified, CleanupExecutionState.ReadyToExecute or CleanupExecutionState.ExecutionFailedBeforeMutation or CleanupExecutionState.CancelledBeforeMutation) => true,
        (CleanupExecutionState.ReadyToExecute, CleanupExecutionState.Executing or CleanupExecutionState.Verifying or CleanupExecutionState.ExecutionFailedBeforeMutation or CleanupExecutionState.CancelledBeforeMutation) => true,
        (CleanupExecutionState.Executing, CleanupExecutionState.Verifying or CleanupExecutionState.ExecutionPartiallyApplied or CleanupExecutionState.VerificationFailed or CleanupExecutionState.RecoveryRequired) => true,
        (CleanupExecutionState.ExecutionPartiallyApplied, CleanupExecutionState.RecoveryRequired) => true,
        (CleanupExecutionState.VerificationFailed, CleanupExecutionState.RecoveryRequired) => true,
        (CleanupExecutionState.Verifying, CleanupExecutionState.Completed or CleanupExecutionState.VerificationFailed or CleanupExecutionState.RecoveryRequired) => true,
        _ => false,
    };

    private bool DependenciesSatisfied(CleanupExecutionOperation operation) => operation.DependencyIds.All(dependency =>
        Operations.Single(value => value.Operation.Id == dependency).Status is ExecutionOperationStatus.Verified or ExecutionOperationStatus.SatisfiedNoOp);

    private int Find(ExecutionOperationId id)
    {
        int index = Operations.ToList().FindIndex(value => value.Operation.Id == id);
        if (index < 0) throw new ArgumentException("The operation is not in this execution.", nameof(id));
        return index;
    }

    private CleanupExecution Replace(int index, ExecutionOperationProgress value)
    {
        ExecutionOperationProgress[] operations = Operations.ToArray();
        operations[index] = value;
        return Copy(operations: operations);
    }

    private CleanupExecution Copy(
        CleanupExecutionState? state = null,
        bool? mutationStarted = null,
        VerifiedBackupManifest? backupManifest = null,
        IEnumerable<ExecutionOperationProgress>? operations = null,
        CleanupExecutionFailureClassification? failureClassification = null,
        CleanupExecutionDisposition? disposition = null) =>
        new(Id, PlanId, PlanContentDigest, Confirmation, Graph, state ?? State,
            mutationStarted ?? MutationStarted, backupManifest ?? BackupManifest, operations ?? Operations,
            failureClassification ?? FailureClassification, disposition ?? Disposition);

    private static CleanupExecutionDisposition DispositionFor(CleanupExecutionState state, bool mutationStarted) => state switch
    {
        CleanupExecutionState.Completed => CleanupExecutionDisposition.Completed,
        CleanupExecutionState.CancelledBeforeMutation => CleanupExecutionDisposition.CancelledBeforeMutation,
        CleanupExecutionState.ExecutionPartiallyApplied => CleanupExecutionDisposition.PartiallyApplied,
        CleanupExecutionState.VerificationFailed => CleanupExecutionDisposition.VerificationFailed,
        CleanupExecutionState.RecoveryRequired => CleanupExecutionDisposition.RecoveryRequired,
        CleanupExecutionState.PreflightFailed or CleanupExecutionState.BackupFailed or CleanupExecutionState.ExecutionFailedBeforeMutation =>
            mutationStarted ? CleanupExecutionDisposition.RecoveryRequired : CleanupExecutionDisposition.Failed,
        _ => CleanupExecutionDisposition.InProgress,
    };
}
