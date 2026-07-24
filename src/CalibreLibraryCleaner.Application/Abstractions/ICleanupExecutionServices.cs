using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ICleanupExecutionIdGenerator
{
    CleanupExecutionId Create();
}

public interface IDestructiveExecutionConfirmation
{
    Task<bool> ConfirmAsync(
        DestructiveExecutionConfirmationRequest request,
        CancellationToken cancellationToken);
}

public interface IPrepareCleanupExecution
{
    Task<CleanupExecutionPreparation> ExecuteAsync(
        PrepareCleanupExecutionRequest request,
        IProgress<Libraries.LibraryScanProgress>? scanProgress,
        CancellationToken cancellationToken);
}

public interface IExecuteApprovedCleanupPlan
{
    Task<CleanupExecutionResult> ExecuteAsync(
        ExecuteCleanupPlanRequest request,
        IProgress<CleanupExecutionProgress>? progress,
        CancellationToken cancellationToken);
}
