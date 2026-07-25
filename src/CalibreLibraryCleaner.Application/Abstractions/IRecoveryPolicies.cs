using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryEligibilityValidator
{
    RecoveryEligibilityResult Evaluate(EvaluateRecoveryEligibilityRequest request, DateTimeOffset evaluatedAtUtc);
}

public interface ICurrentStateReconciler
{
    CurrentStateReconciliation Reconcile(
        RecoverySourceInspection source,
        RecoveryCurrentStateSnapshot currentState);
}

public interface IRecoveryPlanGenerator
{
    RecoveryPlan Generate(GenerateRecoveryPlanRequest request, DateTimeOffset generatedAtUtc);
}

public interface IRecoveryPlanValidator
{
    RecoveryPlanValidationResult Validate(RecoveryPlanDefinition definition, DateTimeOffset validatedAtUtc);
}

public interface IRecoveryStateVerifier
{
    RecoveryVerificationResult Verify(
        ExpectedRecoveredState expected,
        Domain.Libraries.LibrarySnapshot current,
        IEnumerable<RecoveryRecordIdMapping> mappings,
        DateTimeOffset verifiedAtUtc);
}

public interface IPrepareRecoveryExecution
{
    Task<RecoveryExecutionPreparation> ExecuteAsync(
        PrepareRecoveryExecutionRequest request,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IExecuteApprovedRecoveryPlan
{
    Task<RecoveryExecutionResult> ExecuteAsync(
        ExecuteRecoveryPlanRequest request,
        IProgress<RecoveryProgress>? progress,
        CancellationToken cancellationToken);
}
