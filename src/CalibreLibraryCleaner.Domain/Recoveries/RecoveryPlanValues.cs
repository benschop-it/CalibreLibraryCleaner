using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public sealed record RecoveryPlanId
{
    public RecoveryPlanId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A recovery plan ID cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public sealed record RecoveryExecutionId
{
    public RecoveryExecutionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A recovery execution ID cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public sealed record RecoveryPlanSchemaVersion
{
    public static RecoveryPlanSchemaVersion V1 { get; } = new("cleanup-recovery-plan/1.0");

    public RecoveryPlanSchemaVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record RecoveryModelVersion
{
    public static RecoveryModelVersion V1 { get; } = new("cleanup-recovery-model/1.0.0");

    public RecoveryModelVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record RecoveryPolicyVersion
{
    public static RecoveryPolicyVersion V1 { get; } = new("cleanup-recovery-policy/1.0.0");

    public RecoveryPolicyVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record ReconciliationModelVersion
{
    public static ReconciliationModelVersion V1 { get; } = new("cleanup-reconciliation/1.0");

    public ReconciliationModelVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct RecoveryPlanArtifactRevision
{
    public RecoveryPlanArtifactRevision(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public int Value { get; }
}

public sealed record RecoveryPlanContentDigest
{
    public RecoveryPlanContentDigest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string canonical = value.Trim().ToLowerInvariant();
        if (canonical.Length != 64 || canonical.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A recovery plan digest must be a lowercase SHA-256 value.", nameof(value));
        Value = canonical;
    }

    public string Value { get; }
    public override string ToString() => Value;

    internal static RecoveryPlanContentDigest FromCanonical(string canonical) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant());
}

public enum RecoveryPlanState
{
    Draft,
    Blocked,
    Valid,
    Approved,
    Stale,
    Revoked,
    Completed,
}

public sealed record RecoveryInputIdentity
{
    public RecoveryInputIdentity(
        CleanupPlanId sourcePlanId,
        CleanupPlanSchemaVersion sourcePlanSchemaVersion,
        CleanupPlanArtifactRevision sourcePlanRevision,
        CleanupPlanContentDigest sourcePlanContentDigest,
        CleanupExecutionId sourceExecutionId,
        string sourceJournalSchema,
        Sha256Digest sourceJournalFileDigest,
        string sourceJournalFinalEntryHash,
        Sha256Digest? sourceTerminalSummaryDigest,
        string originalManifestSchema,
        Sha256Digest originalManifestInternalDigest,
        Sha256Digest originalManifestFileDigest,
        string sourceApplicationVersion,
        ExecutionToolIdentity sourceToolIdentity,
        string currentLibraryUuid,
        int currentLibrarySchemaVersion,
        Sha256Digest canonicalRootIdentityDigest,
        Sha256Digest fullStateFingerprint,
        Sha256Digest affectedStateFingerprint,
        Sha256Digest unrelatedStateFingerprint,
        ReconciliationModelVersion reconciliationVersion,
        Sha256Digest reconciliationDigest,
        string recoveryCapabilityProfile)
    {
        SourcePlanId = sourcePlanId ?? throw new ArgumentNullException(nameof(sourcePlanId));
        SourcePlanSchemaVersion = sourcePlanSchemaVersion ?? throw new ArgumentNullException(nameof(sourcePlanSchemaVersion));
        SourcePlanRevision = sourcePlanRevision;
        SourcePlanContentDigest = sourcePlanContentDigest ?? throw new ArgumentNullException(nameof(sourcePlanContentDigest));
        SourceExecutionId = sourceExecutionId ?? throw new ArgumentNullException(nameof(sourceExecutionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJournalSchema);
        ValidateDigest(sourceJournalFileDigest, nameof(sourceJournalFileDigest));
        ValidateHashText(sourceJournalFinalEntryHash, nameof(sourceJournalFinalEntryHash));
        if (sourceTerminalSummaryDigest is not null) ValidateDigest(sourceTerminalSummaryDigest.Value, nameof(sourceTerminalSummaryDigest));
        ArgumentException.ThrowIfNullOrWhiteSpace(originalManifestSchema);
        ValidateDigest(originalManifestInternalDigest, nameof(originalManifestInternalDigest));
        ValidateDigest(originalManifestFileDigest, nameof(originalManifestFileDigest));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApplicationVersion);
        SourceToolIdentity = sourceToolIdentity ?? throw new ArgumentNullException(nameof(sourceToolIdentity));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLibraryUuid);
        if (!Guid.TryParse(currentLibraryUuid, out _))
            throw new ArgumentException("The recovery library UUID is invalid.", nameof(currentLibraryUuid));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentLibrarySchemaVersion);
        ValidateDigest(canonicalRootIdentityDigest, nameof(canonicalRootIdentityDigest));
        ValidateDigest(fullStateFingerprint, nameof(fullStateFingerprint));
        ValidateDigest(affectedStateFingerprint, nameof(affectedStateFingerprint));
        ValidateDigest(unrelatedStateFingerprint, nameof(unrelatedStateFingerprint));
        ReconciliationVersion = reconciliationVersion ?? throw new ArgumentNullException(nameof(reconciliationVersion));
        ValidateDigest(reconciliationDigest, nameof(reconciliationDigest));
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryCapabilityProfile);

        SourceJournalSchema = sourceJournalSchema.Trim();
        SourceJournalFileDigest = sourceJournalFileDigest;
        SourceJournalFinalEntryHash = sourceJournalFinalEntryHash.Trim().ToLowerInvariant();
        SourceTerminalSummaryDigest = sourceTerminalSummaryDigest;
        OriginalManifestSchema = originalManifestSchema.Trim();
        OriginalManifestInternalDigest = originalManifestInternalDigest;
        OriginalManifestFileDigest = originalManifestFileDigest;
        SourceApplicationVersion = sourceApplicationVersion.Trim();
        CurrentLibraryUuid = currentLibraryUuid.Trim();
        CurrentLibrarySchemaVersion = currentLibrarySchemaVersion;
        CanonicalRootIdentityDigest = canonicalRootIdentityDigest;
        FullStateFingerprint = fullStateFingerprint;
        AffectedStateFingerprint = affectedStateFingerprint;
        UnrelatedStateFingerprint = unrelatedStateFingerprint;
        ReconciliationDigest = reconciliationDigest;
        RecoveryCapabilityProfile = recoveryCapabilityProfile.Trim();
    }

    public CleanupPlanId SourcePlanId { get; }
    public CleanupPlanSchemaVersion SourcePlanSchemaVersion { get; }
    public CleanupPlanArtifactRevision SourcePlanRevision { get; }
    public CleanupPlanContentDigest SourcePlanContentDigest { get; }
    public CleanupExecutionId SourceExecutionId { get; }
    public string SourceJournalSchema { get; }
    public Sha256Digest SourceJournalFileDigest { get; }
    public string SourceJournalFinalEntryHash { get; }
    public Sha256Digest? SourceTerminalSummaryDigest { get; }
    public string OriginalManifestSchema { get; }
    public Sha256Digest OriginalManifestInternalDigest { get; }
    public Sha256Digest OriginalManifestFileDigest { get; }
    public string SourceApplicationVersion { get; }
    public ExecutionToolIdentity SourceToolIdentity { get; }
    public string CurrentLibraryUuid { get; }
    public int CurrentLibrarySchemaVersion { get; }
    public Sha256Digest CanonicalRootIdentityDigest { get; }
    public Sha256Digest FullStateFingerprint { get; }
    public Sha256Digest AffectedStateFingerprint { get; }
    public Sha256Digest UnrelatedStateFingerprint { get; }
    public ReconciliationModelVersion ReconciliationVersion { get; }
    public Sha256Digest ReconciliationDigest { get; }
    public string RecoveryCapabilityProfile { get; }

    private static void ValidateDigest(Sha256Digest digest, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(digest.Value))
            throw new ArgumentException("A recovery identity digest is required.", parameterName);
    }

    private static void ValidateHashText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string canonical = value.Trim();
        if (canonical.Length != 64 || canonical.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A journal entry hash must be SHA-256.", parameterName);
    }
}

public sealed record RecoveryProvenance(
    CleanupPlanId SourceCleanupPlanId,
    CleanupExecutionId SourceExecutionId,
    DateTimeOffset ReconciledAtUtc,
    string SourceDisposition,
    string SourceBundleIdentity)
{
    public DateTimeOffset ReconciledAtUtc { get; init; } = ReconciledAtUtc.ToUniversalTime();
}
