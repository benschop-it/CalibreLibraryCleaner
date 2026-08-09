using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed record MetadataCandidateCleanupSelection(
    ExactMetadataDuplicateGroupId GroupId,
    CalibreBookId? KeeperBookId,
    bool Skip,
    bool KeeperWasOverridden = false);

public sealed record ExecuteBulkMetadataCandidateCleanupRequest(
    string LibraryRoot,
    IReadOnlyList<MetadataCandidateCleanupSelection> GroupSelections,
    bool ExternalBackupConfirmed);

public enum BulkMetadataCandidateCleanupState
{
    Completed,
    NothingToDo,
    PreflightFailed,
    PartiallyCompleted,
    Cancelled,
}

public sealed record BulkMetadataCandidateCleanupProgress(
    string Message,
    int CompletedOperations,
    int TotalOperations);

public sealed record BulkMetadataCandidateCleanupResult(
    CleanupExecutionId ExecutionId,
    BulkMetadataCandidateCleanupState State,
    int TransferredFormatCount,
    int RemovedFormatCount,
    int RemovedRecordCount,
    int SkippedGroupCount,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsCompleted => State is BulkMetadataCandidateCleanupState.Completed
        or BulkMetadataCandidateCleanupState.NothingToDo;
}
