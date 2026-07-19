using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed record CleanupPlanSummaryRowViewModel(
    CleanupPlan Plan,
    string PlanId,
    int Revision,
    string State,
    string GroupId,
    long TargetRecordId,
    string Digest,
    string ApprovalState,
    bool IsImported);

public sealed record CleanupPlanFormatRetentionRowViewModel(
    string Format,
    long SourceRecordId,
    long TargetRecordId,
    string SourcePath,
    long Length,
    string Sha256,
    string Mode,
    string Preconditions);

public sealed record CleanupPlanFormatRemovalRowViewModel(
    long RecordId,
    string Format,
    string RelativePath,
    string Reason,
    string RetainedInstruction,
    string BackupRequirement,
    bool ByteIdentical);

public sealed record CleanupPlanRecordRemovalRowViewModel(
    long RecordId,
    string BackupRequirements,
    string RequiredRetentions,
    string Preconditions);

public sealed record CleanupPlanExpectedRecordRowViewModel(
    long RecordId,
    string Title,
    string Authors,
    string Identifiers,
    string Publication,
    string Languages,
    string Cover,
    string RelativeDirectory);

public sealed record CleanupPlanExpectedFormatRowViewModel(
    long RecordId,
    string Format,
    string StoredName,
    string RelativePath,
    string Status,
    long Length,
    string Sha256,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    int Attributes,
    string ObservationSourceVersion);

public sealed record CleanupPlanBackupRowViewModel(
    string Id,
    string Kind,
    long? RecordId,
    string? Format,
    string Status,
    string Explanation);

public sealed record CleanupPlanIssueRowViewModel(
    string Severity,
    string Code,
    string Subject,
    string Explanation);

public sealed record CleanupPlanLifecycleRowViewModel(
    int Revision,
    string Transition,
    DateTimeOffset ChangedAtUtc,
    string Reason);
