using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed record ExactDuplicateKeeperSelection(
    ExactBinaryDuplicateGroupId GroupId,
    ExactBinaryDuplicateMember RetainedMember);

public sealed record ExecuteBulkExactDuplicateCleanupRequest(
    string LibraryRoot,
    IReadOnlyList<ExactDuplicateKeeperSelection> KeeperSelections);

public enum BulkExactDuplicateCleanupState
{
    Completed,
    NothingToDo,
    PreflightFailed,
    PartiallyCompleted,
    Cancelled,
}

public sealed record BulkExactDuplicateCleanupProgress(
    string Message,
    int CompletedOperations,
    int TotalOperations);

public sealed record BulkExactDuplicateCleanupResult(
    CleanupExecutionId ExecutionId,
    BulkExactDuplicateCleanupState State,
    int RemovedFormatCount,
    int MergedRecordCount,
    int RemovedRecordCount,
    int SkippedRecordCount,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsCompleted => State is BulkExactDuplicateCleanupState.Completed
        or BulkExactDuplicateCleanupState.NothingToDo;
}
