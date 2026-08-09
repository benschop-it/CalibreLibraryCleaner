using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed record ExpandedCandidateCleanupSelection(
    WorkLanguageCandidateGroupId GroupId,
    CalibreBookId? KeeperBookId,
    bool Skip);

public sealed record ExecuteBulkExpandedCandidateCleanupRequest(
    string LibraryRoot,
    IReadOnlyList<ExpandedCandidateCleanupSelection> GroupSelections,
    bool ExternalBackupConfirmed);

public enum BulkExpandedCandidateCleanupState
{
    NothingToDo,
    PreflightFailed,
    Cancelled,
    PartiallyCompleted,
    Completed,
}

public sealed record BulkExpandedCandidateCleanupProgress(
    string Message,
    int CompletedOperations,
    int TotalOperations);

public sealed record BulkExpandedCandidateCleanupResult(
    CleanupExecutionId ExecutionId,
    BulkExpandedCandidateCleanupState State,
    int TransferredFormatCount,
    int RemovedFormatCount,
    int RemovedRecordCount,
    int SkippedGroupCount,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsCompleted => State is BulkExpandedCandidateCleanupState.Completed
        or BulkExpandedCandidateCleanupState.NothingToDo;
}
