using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Executions;

public enum CompositeCleanupCategory
{
    Exact,
    Metadata,
    Expanded,
    CrossCategory,
}

public sealed record CompositeCleanupConflict
{
    public CompositeCleanupConflict(
        string code,
        CompositeCleanupCategory category,
        string description,
        IEnumerable<CalibreBookId>? recordIds = null,
        string? format = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        Code = code;
        Category = category;
        Description = description;
        RecordIds = new ReadOnlyCollection<CalibreBookId>((recordIds ?? [])
            .Distinct().OrderBy(value => value.Value).ToArray());
        Format = format;
    }

    public string Code { get; }
    public CompositeCleanupCategory Category { get; }
    public string Description { get; }
    public IReadOnlyList<CalibreBookId> RecordIds { get; }
    public string? Format { get; }
}

public sealed record BuildCompositeCleanupRequest(
    string LibraryRoot,
    IReadOnlyList<ExactDuplicateKeeperSelection> ExactSelections,
    IReadOnlyList<MetadataCandidateCleanupSelection> MetadataSelections,
    IReadOnlyList<ExpandedCandidateCleanupSelection> ExpandedSelections);

public sealed record ExecuteCompositeCleanupRequest(
    string LibraryRoot,
    IReadOnlyList<ExactDuplicateKeeperSelection> ExactSelections,
    IReadOnlyList<MetadataCandidateCleanupSelection> MetadataSelections,
    IReadOnlyList<ExpandedCandidateCleanupSelection> ExpandedSelections,
    bool ExternalBackupConfirmed);

public sealed record CompositeCleanupPlanSummary(
    int ExactSelectionCount,
    int MetadataSelectionCount,
    int ExpandedSelectionCount,
    int TransferCount,
    int FormatRemovalCount,
    int RecordRemovalCount,
    int TotalOperationCount);

public sealed record CompositeCleanupBuildResult(
    CompositeCleanupPlanSummary? Summary,
    IReadOnlyList<CompositeCleanupConflict> Conflicts)
{
    public bool IsValid => Summary is not null && Conflicts.Count == 0;
}

public enum CompositeCleanupState
{
    Conflict,
    NothingToDo,
    PreflightFailed,
    Cancelled,
    PartiallyCompleted,
    Completed,
}

public sealed record CompositeCleanupProgress(
    string Message,
    int CompletedOperations,
    int TotalOperations);

public sealed record CompositeCleanupResult(
    CleanupExecutionId ExecutionId,
    CompositeCleanupState State,
    int TransferredFormatCount,
    int RemovedFormatCount,
    int RemovedRecordCount,
    IReadOnlyList<CompositeCleanupConflict> Conflicts,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsCompleted => State is CompositeCleanupState.Completed or CompositeCleanupState.NothingToDo;
}
