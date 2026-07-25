using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public sealed class RecoveryPlanValidator : IRecoveryPlanValidator
{
    public RecoveryPlanValidationResult Validate(
        RecoveryPlanDefinition definition,
        DateTimeOffset validatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<RecoveryIssue> issues = [.. definition.Issues];
        if (definition.InputIdentity.SourcePlanId != definition.OriginalBackupChain.SourcePlanId
            || definition.InputIdentity.SourceExecutionId != definition.OriginalBackupChain.SourceExecutionId)
            issues.Add(Block("RECOVERY.PLAN_SOURCE_CHAIN_MISMATCH",
                "Recovery plan", "The immutable recovery body and original backup chain disagree."));
        if (definition.InputIdentity.ReconciliationDigest != definition.Reconciliation.Digest
            || definition.InputIdentity.FullStateFingerprint != definition.Reconciliation.FullStateFingerprint)
            issues.Add(Block("RECOVERY.PLAN_RECONCILIATION_MISMATCH",
                "Recovery plan", "The recovery body is not bound to the exact current-state reconciliation."));
        if (definition.OperationGraph.DestructiveOperations.Any()
            && definition.OperationGraph.ConstructiveOperations.Count == 0
            && definition.OperationGraph.NonMutatingOperations.Count == 0)
            issues.Add(Block("RECOVERY.DESTRUCTIVE_BARRIER_MISSING",
                "Recovery plan", "Destructive recovery has no constructive or intermediate verification dependency."));
        if (definition.OperationGraph.Operations.Any(value =>
                value.Kind == RecoveryOperationKind.ManualInterventionRequired)
            && definition.OperationGraph.Operations.Any(value => value.IsDispatchable))
            issues.Add(Block("RECOVERY.MANUAL_AND_AUTOMATED_OPERATIONS_CONFLICT",
                "Recovery plan", "A manual-intervention plan cannot dispatch automatic mutations."));
        if (definition.OperationGraph.Operations.Any(value =>
                value.Kind == RecoveryOperationKind.RestoreCoverFromBackup))
            issues.Add(Block("RECOVERY.COVER_RESTORE_NOT_DISPATCHABLE",
                "Recovery plan", "Cover restoration has no qualified executor mapping."));
        if (definition.OperationGraph.Operations.Any(value =>
                value.Kind == RecoveryOperationKind.RemoveRecordCreatedByExecution))
            issues.Add(Block("RECOVERY.RECORD_REMOVAL_IDENTITY_INCOMPLETE",
                "Recovery plan",
                "Automatic record removal is blocked until the plan carries an exact current semantic record identity."));
        if (definition.OperationGraph.Operations.Any(value =>
                value.Kind == RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite))
            issues.Add(Block("RECOVERY.RECOVERY_COPY_OPERATION_NOT_DISPATCHABLE",
                "Recovery plan",
                "The V1 executor has no standalone recovery-copy command mapping."));
        if (definition.OperationGraph.Operations.Any(value =>
                value.Kind == RecoveryOperationKind.ManualInterventionRequired))
            issues.Add(Block("RECOVERY.MANUAL_INTERVENTION_REQUIRED",
                "Recovery plan",
                "A manual-intervention operation can never be approved for automatic execution."));
        return new(issues, validatedAtUtc, definition.InputIdentity);
    }

    private static RecoveryIssue Block(string code, string subject, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation);
}
