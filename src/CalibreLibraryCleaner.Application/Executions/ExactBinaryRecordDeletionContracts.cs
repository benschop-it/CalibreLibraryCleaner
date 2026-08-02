using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed record ExactBinaryRecordBackupInputs(
    ExecutionWorkspace Workspace,
    IReadOnlyDictionary<CalibreBookId, string> ExportDirectories,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsSuccess => Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record ExactBinaryRecordBackupEntry(
    string RelativePath,
    long SizeInBytes,
    Sha256Digest Sha256);

public sealed record ExactBinaryRecordBackupManifest(
    CleanupExecutionId ExecutionId,
    CleanupPlanId PlanId,
    CleanupPlanContentDigest PlanDigest,
    DateTimeOffset VerifiedAtUtc,
    IReadOnlyList<ExactBinaryRecordBackupEntry> Entries,
    Sha256Digest ManifestDigest);

public sealed record ExactBinaryRecordBackupResult(
    ExactBinaryRecordBackupManifest? Manifest,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsSuccess => Manifest is not null
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record ExactBinaryRecordAuditEvent(
    DateTimeOffset OccurredAtUtc,
    string Kind,
    string Message,
    CalibreBookId? RecordId = null,
    string? CommandKind = null,
    int? ExitCode = null,
    string? FailureCode = null);

public interface IExactBinaryRecordBackupStore
{
    Task<ExactBinaryRecordBackupInputs> CreateInputsAsync(
        ExecutionWorkspace workspace,
        ExactBinaryCleanupPlan plan,
        string libraryRoot,
        CalibreToolDescriptor tool,
        string applicationVersion,
        CancellationToken cancellationToken);

    Task<ExactBinaryRecordBackupResult> VerifyAndSealAsync(
        ExactBinaryRecordBackupInputs inputs,
        ExactBinaryCleanupPlan plan,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionIssue>> VerifyAvailableAsync(
        ExecutionWorkspace workspace,
        ExactBinaryRecordBackupManifest manifest,
        CancellationToken cancellationToken);

    Task AppendAuditAsync(
        ExecutionWorkspace workspace,
        ExactBinaryRecordAuditEvent auditEvent,
        CancellationToken cancellationToken);
}

public interface IExactBinaryRecordDeletionConfirmation
{
    Task<bool> ConfirmAsync(
        ExactBinaryCleanupPlan plan,
        ExactBinaryRecordBackupManifest manifest,
        CancellationToken cancellationToken);
}

public sealed record PrepareExactBinaryRecordDeletionRequest(
    ExactBinaryCleanupPlan Plan,
    string LibraryRoot,
    string BackupDestination);

public sealed record ExactBinaryRecordDeletionPreparation(
    ExactBinaryCleanupPlan Plan,
    CalibreToolDescriptor? Tool,
    string? CanonicalLibraryRootIdentity,
    string? CanonicalBackupDestinationIdentity,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsReady => Tool is not null
        && CanonicalLibraryRootIdentity is not null
        && CanonicalBackupDestinationIdentity is not null
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record ExecuteExactBinaryRecordDeletionRequest(
    ExactBinaryCleanupPlan Plan,
    string LibraryRoot,
    string BackupDestination,
    string ApplicationVersion,
    bool OtherCalibreMutatorsClosed,
    bool FullLibraryCopyAcknowledged);

public enum ExactBinaryRecordDeletionState
{
    Completed,
    CancelledBeforeMutation,
    PreflightFailed,
    BackupFailed,
    PartiallyApplied,
    VerificationFailed,
}

public sealed record ExactBinaryRecordDeletionProgress(
    string Message,
    int CompletedOperations,
    int TotalOperations,
    bool MutationStarted);

public sealed record ExactBinaryRecordDeletionResult(
    CleanupExecutionId ExecutionId,
    ExactBinaryRecordDeletionState State,
    IReadOnlyList<ExecutionIssue> Issues,
    string? BundlePath,
    int RemovedFormatCount,
    int RemovedRecordCount,
    bool MutationStarted)
{
    public bool IsCompleted => State == ExactBinaryRecordDeletionState.Completed;
}
