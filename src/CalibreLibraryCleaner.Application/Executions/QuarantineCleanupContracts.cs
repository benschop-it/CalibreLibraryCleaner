using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Executions;

public interface IQuarantineCleanupExecutor
{
    Task<QuarantineCleanupResult> ExecuteAsync(
        ExecuteQuarantineCleanupRequest request,
        IProgress<QuarantineCleanupProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record ExecuteQuarantineCleanupRequest(
    string LibraryRoot,
    LibraryStateGenerationId ExpectedGeneration,
    LibraryStateRevision ExpectedRevision,
    QuarantineReviewWorkspace Review,
    bool ExternalBackupsConfirmed);

public enum QuarantineCleanupState
{
    NothingToDo,
    PreflightFailed,
    Cancelled,
    PartiallyCompleted,
    Completed,
}

public sealed record QuarantineCleanupProgress(
    string Message,
    int CompletedOperations,
    int TotalOperations);

public sealed record QuarantineCleanupResult(
    CleanupExecutionId ExecutionId,
    QuarantineCleanupState State,
    int CopiedRecordCount,
    int RemovedSourceRecordCount,
    string? DestinationLibraryUuid,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsCompleted => State is QuarantineCleanupState.Completed
        or QuarantineCleanupState.NothingToDo;
}
