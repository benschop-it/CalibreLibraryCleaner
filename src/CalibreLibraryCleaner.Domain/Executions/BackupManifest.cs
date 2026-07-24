using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Executions;

public enum BackupArtifactKind
{
    CleanupPlan,
    RecordMetadataOpf,
    Cover,
    RawFormat,
    ExportedFormat,
    ManagedState,
    ToolIdentity,
    ApplicationIdentity,
    PreflightEvidence,
    LocalExecutionConfirmation,
}

public sealed record BackupManifestEntry
{
    public BackupManifestEntry(
        string relativePath,
        BackupArtifactKind kind,
        long sizeInBytes,
        Sha256Digest sha256,
        IEnumerable<string> requirementIds,
        CalibreBookId? recordId = null,
        string? format = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);
        string normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Any(value => value is "" or "." or ".."))
            throw new ArgumentException("Backup manifest paths must be safe relative paths.", nameof(relativePath));
        string[] requirements = requirementIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (requirements.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Backup requirement IDs cannot be empty.", nameof(requirementIds));
        RelativePath = normalized;
        Kind = kind;
        SizeInBytes = sizeInBytes;
        if (string.IsNullOrWhiteSpace(sha256.Value)) throw new ArgumentException("The backup digest is required.", nameof(sha256));
        Sha256 = sha256;
        RequirementIds = Array.AsReadOnly(requirements);
        RecordId = recordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.ToUpperInvariant();
    }

    public string RelativePath { get; }
    public BackupArtifactKind Kind { get; }
    public long SizeInBytes { get; }
    public Sha256Digest Sha256 { get; }
    public IReadOnlyList<string> RequirementIds { get; }
    public CalibreBookId? RecordId { get; }
    public string? Format { get; }
}

public sealed record VerifiedBackupManifest
{
    public const string Version = "execution-backup-manifest/1.0";

    public VerifiedBackupManifest(
        CleanupExecutionId executionId,
        CleanupPlanId planId,
        CleanupPlanContentDigest planContentDigest,
        string libraryUuid,
        DateTimeOffset verifiedAtUtc,
        IEnumerable<BackupManifestEntry> entries,
        Sha256Digest manifestDigest)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(planContentDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        BackupManifestEntry[] ordered = entries.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Select(value => value.RelativePath).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("A backup manifest requires unique entries.", nameof(entries));
        ExecutionId = executionId;
        PlanId = planId;
        PlanContentDigest = planContentDigest;
        LibraryUuid = libraryUuid;
        VerifiedAtUtc = verifiedAtUtc.ToUniversalTime();
        Entries = Array.AsReadOnly(ordered);
        if (string.IsNullOrWhiteSpace(manifestDigest.Value)) throw new ArgumentException("The manifest digest is required.", nameof(manifestDigest));
        ManifestDigest = manifestDigest;
        if (BackupManifestDigestPolicy.Compute(this) != manifestDigest)
            throw new ArgumentException("The backup manifest digest is invalid.", nameof(manifestDigest));
    }

    public CleanupExecutionId ExecutionId { get; }
    public CleanupPlanId PlanId { get; }
    public CleanupPlanContentDigest PlanContentDigest { get; }
    public string LibraryUuid { get; }
    public DateTimeOffset VerifiedAtUtc { get; }
    public IReadOnlyList<BackupManifestEntry> Entries { get; }
    public Sha256Digest ManifestDigest { get; init; }

    public static VerifiedBackupManifest Create(
        CleanupExecutionId executionId,
        CleanupPlan plan,
        DateTimeOffset verifiedAtUtc,
        IEnumerable<BackupManifestEntry> entries)
    {
        BackupManifestEntry[] materialized = entries.ToArray();
        VerifiedBackupManifest unsigned = new(executionId, plan.Id, plan.ContentDigest,
            plan.InputIdentity.LibraryUuid, verifiedAtUtc, materialized, new Sha256Digest(new string('0', 64)), skipDigestValidation: true);
        return new(executionId, plan.Id, plan.ContentDigest, plan.InputIdentity.LibraryUuid,
            verifiedAtUtc, materialized, BackupManifestDigestPolicy.Compute(unsigned));
    }

    private VerifiedBackupManifest(
        CleanupExecutionId executionId,
        CleanupPlanId planId,
        CleanupPlanContentDigest planContentDigest,
        string libraryUuid,
        DateTimeOffset verifiedAtUtc,
        IEnumerable<BackupManifestEntry> entries,
        Sha256Digest manifestDigest,
        bool skipDigestValidation)
    {
        _ = skipDigestValidation;
        ExecutionId = executionId;
        PlanId = planId;
        PlanContentDigest = planContentDigest;
        LibraryUuid = libraryUuid;
        VerifiedAtUtc = verifiedAtUtc.ToUniversalTime();
        Entries = Array.AsReadOnly(entries.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray());
        ManifestDigest = manifestDigest;
    }
}

public static class BackupManifestDigestPolicy
{
    public static Sha256Digest Compute(VerifiedBackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        StringBuilder canonical = new();
        Add(canonical, VerifiedBackupManifest.Version);
        Add(canonical, manifest.ExecutionId.ToString());
        Add(canonical, manifest.PlanId.ToString());
        Add(canonical, manifest.PlanContentDigest.Value);
        Add(canonical, manifest.LibraryUuid);
        Add(canonical, manifest.VerifiedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        foreach (BackupManifestEntry entry in manifest.Entries.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            Add(canonical, entry.RelativePath);
            Add(canonical, entry.Kind.ToString());
            Add(canonical, entry.SizeInBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(canonical, entry.Sha256.Value);
            Add(canonical, entry.RecordId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, entry.Format ?? string.Empty);
            foreach (string requirement in entry.RequirementIds) Add(canonical, requirement);
            Add(canonical, "|");
        }

        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
    }

    private static void Add(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value).Append(';');
}

public static class BackupManifestCoveragePolicy
{
    public static IReadOnlyList<ExecutionIssue> Validate(CleanupPlan plan, VerifiedBackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        List<ExecutionIssue> issues = [];
        if (manifest.PlanId != plan.Id || manifest.PlanContentDigest != plan.ContentDigest
            || !string.Equals(manifest.LibraryUuid, plan.InputIdentity.LibraryUuid, StringComparison.Ordinal))
            issues.Add(new("EXECUTION.BACKUP_IDENTITY_MISMATCH", ExecutionIssueSeverity.BlockingError, "The backup manifest is not bound to the approved plan and library."));
        if (BackupManifestDigestPolicy.Compute(manifest) != manifest.ManifestDigest)
            issues.Add(new("EXECUTION.BACKUP_MANIFEST_TAMPERED", ExecutionIssueSeverity.BlockingError, "The backup manifest digest is invalid."));

        HashSet<string> covered = manifest.Entries.SelectMany(value => value.RequirementIds).ToHashSet(StringComparer.Ordinal);
        foreach (BackupRequirement requirement in plan.Definition.BackupRequirements)
        {
            if (requirement.Kind == BackupRequirementKind.ExecutionAudit) continue;
            if (!covered.Contains(requirement.Id))
                issues.Add(new("EXECUTION.BACKUP_REQUIREMENT_MISSING", ExecutionIssueSeverity.BlockingError,
                    "A mandatory backup requirement is not represented by a verified artifact.", requirement.RecordId, requirement.Format));
        }
        if (manifest.Entries.Count(value => value.Kind == BackupArtifactKind.LocalExecutionConfirmation) != 1)
            issues.Add(new("EXECUTION.BACKUP_CONFIRMATION_MISSING", ExecutionIssueSeverity.BlockingError,
                "The verified backup must contain exactly one local execution confirmation."));

        return issues;
    }
}
