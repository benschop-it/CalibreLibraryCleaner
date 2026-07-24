using System.Globalization;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Executions;

public sealed record CleanupExecutionId
{
    public CleanupExecutionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("An execution ID cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public sealed record CleanupExecutionSchemaVersion
{
    public static CleanupExecutionSchemaVersion V1 { get; } = new("cleanup-execution/1.0");

    public CleanupExecutionSchemaVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record ExecutionToolIdentity
{
    public ExecutionToolIdentity(
        string canonicalExecutableIdentity,
        string productVersion,
        Sha256Digest executableSha256,
        string capabilityProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalExecutableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityProfile);
        CanonicalExecutableIdentity = canonicalExecutableIdentity.Trim();
        ProductVersion = productVersion.Trim();
        if (string.IsNullOrWhiteSpace(executableSha256.Value)) throw new ArgumentException("The executable digest is required.", nameof(executableSha256));
        ExecutableSha256 = executableSha256;
        CapabilityProfile = capabilityProfile.Trim();
    }

    public string CanonicalExecutableIdentity { get; }
    public string ProductVersion { get; }
    public Sha256Digest ExecutableSha256 { get; }
    public string CapabilityProfile { get; }
}

public sealed record CleanupExecutionConfirmation
{
    public CleanupExecutionConfirmation(
        CleanupPlanId planId,
        CleanupPlanArtifactRevision planRevision,
        CleanupPlanContentDigest planContentDigest,
        string libraryUuid,
        string canonicalLibraryRootIdentity,
        Sha256Digest operationGraphDigest,
        ExecutionToolIdentity toolIdentity,
        string backupDestinationIdentity,
        DateTimeOffset confirmedAtUtc,
        bool otherCalibreMutatorsClosed,
        bool recoveryLimitationsAccepted)
    {
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(planContentDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        if (!Guid.TryParse(libraryUuid, out _)) throw new ArgumentException("The confirmed library UUID is invalid.", nameof(libraryUuid));
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalLibraryRootIdentity);
        if (string.IsNullOrWhiteSpace(operationGraphDigest.Value))
            throw new ArgumentException("The confirmed operation graph digest is required.", nameof(operationGraphDigest));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDestinationIdentity);
        if (!otherCalibreMutatorsClosed || !recoveryLimitationsAccepted)
            throw new ArgumentException("Execution confirmation requires both safety acknowledgements.");
        PlanId = planId;
        PlanRevision = planRevision;
        PlanContentDigest = planContentDigest;
        LibraryUuid = libraryUuid.Trim();
        CanonicalLibraryRootIdentity = canonicalLibraryRootIdentity.Trim();
        OperationGraphDigest = operationGraphDigest;
        ToolIdentity = toolIdentity ?? throw new ArgumentNullException(nameof(toolIdentity));
        BackupDestinationIdentity = backupDestinationIdentity.Trim();
        ConfirmedAtUtc = confirmedAtUtc.ToUniversalTime();
        OtherCalibreMutatorsClosed = true;
        RecoveryLimitationsAccepted = true;
    }

    public CleanupPlanId PlanId { get; }
    public CleanupPlanArtifactRevision PlanRevision { get; }
    public CleanupPlanContentDigest PlanContentDigest { get; }
    public string LibraryUuid { get; }
    public string CanonicalLibraryRootIdentity { get; }
    public Sha256Digest OperationGraphDigest { get; }
    public ExecutionToolIdentity ToolIdentity { get; }
    public string BackupDestinationIdentity { get; }
    public DateTimeOffset ConfirmedAtUtc { get; }
    public bool OtherCalibreMutatorsClosed { get; }
    public bool RecoveryLimitationsAccepted { get; }

    public bool Matches(
        CleanupPlan plan,
        ExecutionToolIdentity tool,
        string backupDestinationIdentity,
        string canonicalLibraryRootIdentity,
        Sha256Digest operationGraphDigest) =>
        plan.Id == PlanId
        && plan.ArtifactRevision == PlanRevision
        && plan.ContentDigest == PlanContentDigest
        && string.Equals(plan.InputIdentity.LibraryUuid, LibraryUuid, StringComparison.Ordinal)
        && string.Equals(CanonicalLibraryRootIdentity, canonicalLibraryRootIdentity, StringComparison.OrdinalIgnoreCase)
        && OperationGraphDigest == operationGraphDigest
        && ToolIdentity == tool
        && string.Equals(BackupDestinationIdentity, backupDestinationIdentity, StringComparison.OrdinalIgnoreCase)
        && OtherCalibreMutatorsClosed
        && RecoveryLimitationsAccepted;
}

public enum CleanupExecutionState
{
    Created,
    AcquiringLease,
    PreflightValidating,
    ReadyForBackup,
    BackingUp,
    BackupVerified,
    ReadyToExecute,
    Executing,
    Verifying,
    Completed,
    PreflightFailed,
    BackupFailed,
    ExecutionFailedBeforeMutation,
    ExecutionPartiallyApplied,
    VerificationFailed,
    CancelledBeforeMutation,
    RecoveryRequired,
}

public enum CleanupExecutionFailureClassification
{
    None,
    Preflight,
    Backup,
    CommandBeforeMutation,
    ConstructiveCommand,
    IntermediateVerification,
    DestructiveCommand,
    FinalVerification,
    Journal,
    Cancellation,
    CrashOrIndeterminate,
}

public enum CleanupExecutionDisposition
{
    InProgress,
    Completed,
    Failed,
    PartiallyApplied,
    VerificationFailed,
    RecoveryRequired,
    CancelledBeforeMutation,
}

public enum ExecutionOperationPhase
{
    Precondition,
    Constructive,
    Destructive,
}

public enum ExecutionOperationKind
{
    VerifyMetadataPreserved,
    VerifyTargetFormatPreserved,
    AddOrReplaceFormat,
    RemoveRedundantRecord,
}

public enum ExecutionOperationStatus
{
    Planned,
    Starting,
    Succeeded,
    Verified,
    SatisfiedNoOp,
    Failed,
    NotStarted,
}

public enum ExecutionIssueSeverity
{
    BlockingError,
    Warning,
    Information,
}

public sealed record ExecutionIssue
{
    public ExecutionIssue(
        string code,
        ExecutionIssueSeverity severity,
        string explanation,
        CalibreBookId? recordId = null,
        string? format = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        if (code.Length > 128 || explanation.Length > 1024) throw new ArgumentException("Execution issue text exceeds its bounds.");
        Code = code.Trim();
        Severity = severity;
        Explanation = explanation.Trim();
        RecordId = recordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.ToUpperInvariant();
    }

    public string Code { get; }
    public ExecutionIssueSeverity Severity { get; }
    public string Explanation { get; }
    public CalibreBookId? RecordId { get; }
    public string? Format { get; }
}

public sealed record ExecutionVerificationResult
{
    public ExecutionVerificationResult(IEnumerable<ExecutionIssue> issues, DateTimeOffset verifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues.OrderBy(value => value.Severity)
            .ThenBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.Code, StringComparer.Ordinal).ToArray());
        VerifiedAtUtc = verifiedAtUtc.ToUniversalTime();
    }

    public IReadOnlyList<ExecutionIssue> Issues { get; }
    public DateTimeOffset VerifiedAtUtc { get; }
    public bool IsVerified => Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}
