using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Executions;

public interface IUnifiedCandidateCleanupExecutor
{
    Task<UnifiedCandidateCleanupResult> ExecuteAsync(
        ExecuteUnifiedCandidateCleanupRequest request,
        IProgress<UnifiedCandidateCleanupProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record UnifiedCandidateCleanupSelection(
    UnifiedCandidateGroupId GroupId,
    IReadOnlyList<CalibreBookId> ExpectedMembers,
    CalibreBookId KeeperBookId,
    bool Skip,
    bool KeeperWasOverridden = false);

public sealed record ExecuteUnifiedCandidateCleanupRequest(
    string LibraryRoot,
    LibraryStateGenerationId ExpectedGeneration,
    LibraryStateRevision ExpectedRevision,
    IReadOnlyList<UnifiedCandidateCleanupSelection> GroupSelections,
    bool ExternalBackupConfirmed,
    MetadataReviewWorkspace? MetadataReview = null,
    bool NormalizeAuthors = false);

public enum UnifiedCandidateCleanupState
{
    NothingToDo,
    PreflightFailed,
    Cancelled,
    PartiallyCompleted,
    Completed,
}

public sealed record UnifiedCandidateCleanupProgress(
    string Message,
    int CompletedOperations,
    int TotalOperations);

public sealed record UnifiedCandidateCleanupResult(
    CleanupExecutionId ExecutionId,
    UnifiedCandidateCleanupState State,
    int TransferredFormatCount,
    int RemovedFormatCount,
    int RemovedRecordCount,
    int SkippedGroupCount,
    IReadOnlyList<ExecutionIssue> Issues,
    int UpdatedMetadataFieldCount = 0,
    int SkippedMetadataFieldCount = 0,
    int OmittedMetadataCoverCount = 0,
    int OmittedMetadataAuthorFieldCount = 0)
{
    public bool IsCompleted => State is UnifiedCandidateCleanupState.Completed
        or UnifiedCandidateCleanupState.NothingToDo;
}

public sealed record UnifiedCandidateCleanupPlan(
    IReadOnlyList<UnifiedCandidateTransfer> Transfers,
    IReadOnlyList<UnifiedCandidateFormatRemoval> FormatRemovals,
    IReadOnlyList<CalibreBookId> RecordsToRemove,
    int SkippedGroupCount)
{
    public int TotalOperations => Transfers.Count + FormatRemovals.Count + RecordsToRemove.Count;
}

public sealed record UnifiedCandidateTransfer(
    CalibreBookId SourceRecordId,
    CalibreBookId TargetRecordId,
    BookFormat SourceFormat);

public sealed record UnifiedCandidateFormatRemoval(CalibreBookId RecordId, BookFormat Format);
