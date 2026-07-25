using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public sealed record RecoveryBackupChain
{
    public RecoveryBackupChain(
        CleanupPlanId sourcePlanId,
        CleanupPlanContentDigest sourcePlanContentDigest,
        CleanupExecutionId sourceExecutionId,
        Sha256Digest sourceJournalFileDigest,
        string sourceJournalFinalEntryHash,
        Sha256Digest originalManifestFileDigest,
        Sha256Digest originalManifestInternalDigest,
        RecoveryPlanId recoveryPlanId,
        RecoveryPlanContentDigest? recoveryPlanContentDigest,
        RecoveryExecutionId? recoveryExecutionId = null,
        Sha256Digest? currentStateManifestFileDigest = null,
        Sha256Digest? currentStateManifestInternalDigest = null,
        string? recoveryJournalIdentity = null)
    {
        SourcePlanId = sourcePlanId ?? throw new ArgumentNullException(nameof(sourcePlanId));
        SourcePlanContentDigest = sourcePlanContentDigest ?? throw new ArgumentNullException(nameof(sourcePlanContentDigest));
        SourceExecutionId = sourceExecutionId ?? throw new ArgumentNullException(nameof(sourceExecutionId));
        if (string.IsNullOrWhiteSpace(sourceJournalFileDigest.Value)
            || string.IsNullOrWhiteSpace(originalManifestFileDigest.Value)
            || string.IsNullOrWhiteSpace(originalManifestInternalDigest.Value))
            throw new ArgumentException("The original recovery backup-chain digests are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJournalFinalEntryHash);
        if (sourceJournalFinalEntryHash.Length != 64 || sourceJournalFinalEntryHash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The source journal final-entry hash is invalid.", nameof(sourceJournalFinalEntryHash));
        RecoveryPlanId = recoveryPlanId ?? throw new ArgumentNullException(nameof(recoveryPlanId));
        bool hasCurrentManifest = currentStateManifestFileDigest is not null || currentStateManifestInternalDigest is not null;
        if (hasCurrentManifest && (currentStateManifestFileDigest is null || currentStateManifestInternalDigest is null
                || recoveryExecutionId is null || string.IsNullOrWhiteSpace(recoveryJournalIdentity)))
            throw new ArgumentException("A current-state backup-chain link must be complete.");
        SourceJournalFileDigest = sourceJournalFileDigest;
        SourceJournalFinalEntryHash = sourceJournalFinalEntryHash.Trim().ToLowerInvariant();
        OriginalManifestFileDigest = originalManifestFileDigest;
        OriginalManifestInternalDigest = originalManifestInternalDigest;
        RecoveryPlanContentDigest = recoveryPlanContentDigest;
        RecoveryExecutionId = recoveryExecutionId;
        CurrentStateManifestFileDigest = currentStateManifestFileDigest;
        CurrentStateManifestInternalDigest = currentStateManifestInternalDigest;
        RecoveryJournalIdentity = string.IsNullOrWhiteSpace(recoveryJournalIdentity) ? null : recoveryJournalIdentity.Trim();
    }

    public CleanupPlanId SourcePlanId { get; }
    public CleanupPlanContentDigest SourcePlanContentDigest { get; }
    public CleanupExecutionId SourceExecutionId { get; }
    public Sha256Digest SourceJournalFileDigest { get; }
    public string SourceJournalFinalEntryHash { get; }
    public Sha256Digest OriginalManifestFileDigest { get; }
    public Sha256Digest OriginalManifestInternalDigest { get; }
    public RecoveryPlanId RecoveryPlanId { get; }
    public RecoveryPlanContentDigest? RecoveryPlanContentDigest { get; }
    public RecoveryExecutionId? RecoveryExecutionId { get; }
    public Sha256Digest? CurrentStateManifestFileDigest { get; }
    public Sha256Digest? CurrentStateManifestInternalDigest { get; }
    public string? RecoveryJournalIdentity { get; }
    public bool HasVerifiedCurrentStateLink => CurrentStateManifestFileDigest is not null
        && CurrentStateManifestInternalDigest is not null && RecoveryExecutionId is not null;

    public RecoveryBackupChain BindPlanDigest(RecoveryPlanContentDigest digest) =>
        new(SourcePlanId, SourcePlanContentDigest, SourceExecutionId, SourceJournalFileDigest,
            SourceJournalFinalEntryHash, OriginalManifestFileDigest, OriginalManifestInternalDigest,
            RecoveryPlanId, digest, RecoveryExecutionId, CurrentStateManifestFileDigest,
            CurrentStateManifestInternalDigest, RecoveryJournalIdentity);

    public RecoveryBackupChain BindCurrentState(
        RecoveryExecutionId executionId,
        Sha256Digest manifestFileDigest,
        Sha256Digest manifestInternalDigest,
        string journalIdentity) =>
        new(SourcePlanId, SourcePlanContentDigest, SourceExecutionId, SourceJournalFileDigest,
            SourceJournalFinalEntryHash, OriginalManifestFileDigest, OriginalManifestInternalDigest,
            RecoveryPlanId, RecoveryPlanContentDigest, executionId, manifestFileDigest,
            manifestInternalDigest, journalIdentity);
}
