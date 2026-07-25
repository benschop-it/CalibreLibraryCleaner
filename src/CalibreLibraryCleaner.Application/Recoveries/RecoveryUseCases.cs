using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public sealed class InspectRecoverySourceExecutionUseCase(IRecoverySourceArtifactReader reader)
{
    public Task<RecoverySourceInspection> ExecuteAsync(
        InspectRecoverySourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return reader.ReadAndVerifyAsync(request.SourceBundle, cancellationToken);
    }
}

public sealed class EvaluateRecoveryEligibilityUseCase(
    IRecoveryEligibilityValidator validator,
    IClock clock)
{
    public RecoveryEligibilityResult Execute(EvaluateRecoveryEligibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return validator.Evaluate(request, clock.GetUtcNow());
    }
}

public sealed record ReconcileCurrentRecoveryStateRequest(
    RecoverySourceInspection Source,
    string LibraryRoot);

public sealed record ReconcileCurrentRecoveryStateResult(
    RecoveryCurrentStateSnapshot? CurrentState,
    CurrentStateReconciliation? Reconciliation,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool IsSuccess => CurrentState is not null && Reconciliation is not null
        && Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
}

public sealed class ReconcileCurrentRecoveryStateUseCase(
    IRecoveryCurrentStateScanner scanner,
    ICurrentStateReconciler reconciler)
{
    public async Task<ReconcileCurrentRecoveryStateResult> ExecuteAsync(
        ReconcileCurrentRecoveryStateRequest request,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source.CleanupPlan is null)
            return new(null, null,
                [new("RECOVERY.SOURCE_PLAN_MISSING", RecoveryIssueSeverity.Blocking,
                    "Source execution", "The verified source cleanup plan is required.")]);
        RecoveryCurrentStateScanResult scan = await scanner.ScanFreshAsync(
            request.LibraryRoot, request.Source.CleanupPlan.Definition.InvolvedRecordIds,
            progress, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
            return new(scan.CurrentState, null, scan.Issues);
        CurrentStateReconciliation reconciliation = reconciler.Reconcile(request.Source, scan.CurrentState!);
        return new(scan.CurrentState, reconciliation,
            scan.Issues.Concat(reconciliation.Issues).Distinct().ToArray());
    }
}

public sealed class GenerateRecoveryPlanUseCase(
    IRecoveryPlanGenerator generator,
    IClock clock)
{
    public RecoveryPlan Execute(GenerateRecoveryPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return generator.Generate(request, clock.GetUtcNow());
    }
}

public sealed class ValidateRecoveryPlanUseCase(
    IRecoveryPlanValidator validator,
    IClock clock)
{
    public RecoveryPlanValidationResult Execute(RecoveryPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return validator.Validate(definition, clock.GetUtcNow());
    }
}

public sealed class ApproveRecoveryPlanUseCase(IClock clock)
{
    public RecoveryPlan Execute(RecoveryPlan plan, IEnumerable<string> acknowledgedWarningCodes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RecoveryPlanLifecyclePolicy.Approve(plan, acknowledgedWarningCodes, clock.GetUtcNow());
    }
}

public sealed class RevokeRecoveryPlanUseCase(IClock clock)
{
    public RecoveryPlan Execute(RecoveryPlan plan, string reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RecoveryPlanLifecyclePolicy.Revoke(plan, reason, clock.GetUtcNow());
    }
}

public sealed class EvaluateRecoveryPlanStalenessUseCase(
    IRecoveryPlanValidator validator,
    IClock clock)
{
    public RecoveryPlan Execute(
        RecoveryPlan plan,
        RecoveryInputIdentity currentInputIdentity)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentInputIdentity);
        if (plan.Definition.InputIdentity == currentInputIdentity) return plan;
        RecoveryPlanValidationResult validation = validator.Validate(plan.Definition, clock.GetUtcNow());
        return RecoveryPlanLifecyclePolicy.MarkStale(plan, validation, clock.GetUtcNow());
    }
}

public sealed class RecoveryStateVerifier : IRecoveryStateVerifier
{
    public RecoveryVerificationResult Verify(
        ExpectedRecoveredState expected,
        Domain.Libraries.LibrarySnapshot current,
        IEnumerable<RecoveryRecordIdMapping> mappings,
        DateTimeOffset verifiedAtUtc) =>
        RecoveryVerificationPolicy.VerifyFinalState(expected, current, mappings, verifiedAtUtc);
}

public sealed class ReadRecoveryHistoryUseCase(IRecoveryHistoryStore history)
{
    public Task<IReadOnlyList<RecoveryHistoryEntry>> ExecuteAsync(
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken) =>
        history.ReadAsync(libraryUuid, libraryRoot, cancellationToken);
}

public sealed class ExportRecoveryArtifactsUseCase(IRecoveryPlanStore store)
{
    public Task<RecoveryPlanStoreResult> ExportPlanAsync(
        RecoveryPlan plan,
        string destinationPath,
        string libraryRoot,
        CancellationToken cancellationToken) =>
        store.WriteCreateNewAsync(plan, destinationPath, libraryRoot, cancellationToken);
}
