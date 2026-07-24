using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Executions;

public enum CalibreExecutionCapability
{
    ExportRecord,
    AddOrReplaceFormat,
    RemoveRecordNonPermanently,
}

public sealed record CalibreToolDescriptor
{
    public CalibreToolDescriptor(
        string canonicalExecutablePath,
        ExecutionToolIdentity identity,
        IEnumerable<CalibreExecutionCapability> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalExecutablePath);
        CanonicalExecutablePath = canonicalExecutablePath;
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Capabilities = Array.AsReadOnly(capabilities.Distinct().Order().ToArray());
    }

    public string CanonicalExecutablePath { get; }
    public ExecutionToolIdentity Identity { get; }
    public IReadOnlyList<CalibreExecutionCapability> Capabilities { get; }

    public bool SupportsAllRequired =>
        Capabilities.Contains(CalibreExecutionCapability.ExportRecord)
        && Capabilities.Contains(CalibreExecutionCapability.AddOrReplaceFormat)
        && Capabilities.Contains(CalibreExecutionCapability.RemoveRecordNonPermanently);
}

public sealed record CalibreToolDiscoveryResult(
    CalibreToolDescriptor? Tool,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsSuccess => Tool is { SupportsAllRequired: true }
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record CalibreCommandResult(
    string CommandKind,
    bool Started,
    int? ExitCode,
    IReadOnlyList<string> SanitizedArguments,
    string SanitizedStandardOutput,
    string SanitizedStandardError,
    TimeSpan Duration,
    string? FailureCode = null)
{
    public bool IsSuccess => Started && ExitCode == 0 && FailureCode is null;
}

public sealed record ExportCalibreRecordRequest(
    CalibreToolDescriptor Tool,
    string LibraryRoot,
    CalibreBookId RecordId,
    string DestinationDirectory);

public sealed record AddOrReplaceCalibreFormatRequest(
    CalibreToolDescriptor Tool,
    string LibraryRoot,
    CalibreBookId TargetRecordId,
    string CanonicalFormat,
    string VerifiedBackupFilePath,
    FormatFileFingerprint ExpectedFingerprint);

public sealed record RemoveCalibreRecordRequest(
    CalibreToolDescriptor Tool,
    string LibraryRoot,
    CalibreBookId RecordId);

public sealed record ExecutionLeaseRequest(
    CleanupExecutionId ExecutionId,
    string LibraryRoot,
    string LibraryUuid,
    DateTimeOffset RequestedAtUtc);

public sealed record ExecutionLeaseAcquisition(
    ICleanupExecutionLeaseHandle? Lease,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsAcquired => Lease is not null
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record BackupDestinationValidation(
    string? CanonicalDestinationIdentity,
    long AvailableBytes,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsValid => CanonicalDestinationIdentity is not null
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record ExecutionWorkspace(
    CleanupExecutionId ExecutionId,
    string BundlePath,
    string CanonicalBackupDestinationIdentity);

public sealed record BackupFormatKey(CalibreBookId RecordId, string Format, string ExpectedRelativePath);

public sealed record ExecutionBackupInputs(
    ExecutionWorkspace Workspace,
    IReadOnlyDictionary<CalibreBookId, string> ExportDirectories,
    IReadOnlyDictionary<BackupFormatKey, string> RawFormatBackupPaths,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsSuccess => Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record ExecutionBackupResult(
    VerifiedBackupManifest? Manifest,
    IReadOnlyDictionary<BackupFormatKey, string> RawFormatBackupPaths,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsSuccess => Manifest is not null
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record CreateBackupInputsRequest(
    ExecutionWorkspace Workspace,
    CleanupPlan Plan,
    CleanupExecutionConfirmation Confirmation,
    string LibraryRoot,
    CalibreToolDescriptor Tool,
    string ApplicationVersion,
    Sha256Digest UnaffectedBaseline,
    DateTimeOffset CreatedAtUtc);

public sealed record SealBackupRequest(
    ExecutionBackupInputs Inputs,
    CleanupPlan Plan,
    string LibraryRoot,
    DateTimeOffset VerifiedAtUtc);

public sealed record ExecutionJournalEvent(
    string Kind,
    CleanupExecutionState State,
    DateTimeOffset OccurredAtUtc,
    string Message,
    ExecutionOperationId? OperationId = null,
    string? MappedCommand = null,
    IReadOnlyList<string>? SanitizedArguments = null,
    int? ExitCode = null,
    string? SanitizedStandardOutput = null,
    string? SanitizedStandardError = null,
    string? FailureCode = null,
    bool MutationStarted = false,
    IReadOnlyList<ExecutionIssue>? Issues = null);

public sealed record ExecutionJournalCreateRequest(
    ExecutionWorkspace Workspace,
    CleanupPlan Plan,
    string LibraryRoot,
    string ApplicationVersion,
    DateTimeOffset CreatedAtUtc);

public sealed record JournalReconciliationResult(
    bool RecoveryRequired,
    IReadOnlyList<ExecutionIssue> Issues);

public sealed record ExecutionHistoryEntry(
    CleanupExecutionId ExecutionId,
    CleanupPlanId PlanId,
    CleanupPlanContentDigest PlanContentDigest,
    string LibraryUuid,
    CleanupExecutionState State,
    CleanupExecutionDisposition Disposition,
    CleanupExecutionFailureClassification FailureClassification,
    string BundlePath,
    string JournalIdentity,
    string? BackupManifestDigest,
    DateTimeOffset FinishedAtUtc,
    bool MutationStarted);

public sealed record DestructiveExecutionConfirmationRequest(
    CleanupExecutionId ExecutionId,
    CleanupPlanId PlanId,
    CleanupPlanContentDigest PlanContentDigest,
    CalibreBookId TargetRecordId,
    IReadOnlyList<CalibreBookId> RecordsToRemove,
    string BackupManifestDigest);

public enum CleanupExecutionProgressPhase
{
    AcquiringLease,
    Preflight,
    Backup,
    FinalMutationGate,
    Constructive,
    IntermediateVerification,
    DestructiveGate,
    Destructive,
    FinalVerification,
    Completed,
    Failed,
}

public sealed record CleanupExecutionProgress(
    CleanupExecutionProgressPhase Phase,
    string Message,
    int CompletedOperations,
    int TotalOperations,
    bool MutationStarted,
    ExecutionOperationId? OperationId = null);

public sealed record PrepareCleanupExecutionRequest(
    CleanupPlan Plan,
    string LibraryRoot,
    string BackupDestination);

public sealed record CleanupExecutionPreparation(
    CleanupPlan Plan,
    CalibreToolDescriptor? Tool,
    CleanupExecutionOperationGraph? OperationGraph,
    string? CanonicalLibraryRootIdentity,
    string? CanonicalBackupDestinationIdentity,
    IReadOnlyList<ExecutionIssue> Issues,
    DateTimeOffset PreparedAtUtc)
{
    public bool IsReady => Tool is not null && OperationGraph is not null
        && CanonicalLibraryRootIdentity is not null
        && CanonicalBackupDestinationIdentity is not null
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record ExecuteCleanupPlanRequest(
    CleanupPlan Plan,
    string LibraryRoot,
    string BackupDestination,
    CleanupExecutionConfirmation Confirmation,
    string ApplicationVersion);

public sealed record CleanupExecutionResult(
    CleanupExecutionId ExecutionId,
    CleanupExecutionState State,
    CleanupExecutionDisposition Disposition,
    CleanupExecutionFailureClassification FailureClassification,
    IReadOnlyList<ExecutionIssue> Issues,
    string? BundlePath,
    string? JournalIdentity,
    string? BackupManifestDigest,
    bool MutationStarted)
{
    public bool IsCompleted => State == CleanupExecutionState.Completed
        && Disposition == CleanupExecutionDisposition.Completed;
}
