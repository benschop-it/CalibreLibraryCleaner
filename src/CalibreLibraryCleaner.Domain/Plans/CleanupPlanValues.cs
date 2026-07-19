using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Domain.Plans;

public sealed record CleanupPlanId
{
    public CleanupPlanId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A cleanup plan ID cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public sealed record CleanupPlanSchemaVersion
{
    public static CleanupPlanSchemaVersion V1 { get; } = new("cleanup-plan/1.0");
    public CleanupPlanSchemaVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record CleanupPlanPolicyVersion
{
    public static CleanupPlanPolicyVersion V1 { get; } = new("cleanup-plan-policy/1.0.0");
    public CleanupPlanPolicyVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct CleanupPlanArtifactRevision
{
    public CleanupPlanArtifactRevision(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public int Value { get; }
}

public enum CleanupPlanState
{
    Draft,
    Valid,
    Blocked,
    Approved,
    Stale,
    Revoked,
}

public sealed record CleanupPlanContentDigest
{
    public CleanupPlanContentDigest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string canonical = value.Trim().ToLowerInvariant();
        if (canonical.Length != 64 || canonical.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A cleanup plan digest must be a lowercase SHA-256 value.", nameof(value));
        }

        Value = canonical;
    }

    public string Value { get; }
    public override string ToString() => Value;

    internal static CleanupPlanContentDigest FromCanonical(string canonical) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant());
}

public sealed record CleanupPlanInputIdentity
{
    public CleanupPlanInputIdentity(
        string libraryUuid,
        int schemaVersion,
        ExactMetadataDuplicateGroupId groupId,
        IEnumerable<CalibreBookId> memberIds,
        RecommendationModelVersion recommendationModelVersion,
        RecommendationInputVersion recommendationInputVersion,
        CleanupPlanPolicyVersion policyVersion,
        CleanupPlanContentDigest definitionDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        if (!Guid.TryParse(libraryUuid, out _)) throw new ArgumentException("The cleanup plan library UUID is invalid.", nameof(libraryUuid));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentNullException.ThrowIfNull(memberIds);
        CalibreBookId[] members = memberIds.Distinct().OrderBy(value => value.Value).ToArray();
        if (members.Length < 2) throw new ArgumentException("A cleanup plan input requires a duplicate group.", nameof(memberIds));
        LibraryUuid = libraryUuid.Trim();
        SchemaVersion = schemaVersion;
        GroupId = groupId;
        MemberIds = Array.AsReadOnly(members);
        RecommendationModelVersion = recommendationModelVersion ?? throw new ArgumentNullException(nameof(recommendationModelVersion));
        RecommendationInputVersion = recommendationInputVersion ?? throw new ArgumentNullException(nameof(recommendationInputVersion));
        PolicyVersion = policyVersion ?? throw new ArgumentNullException(nameof(policyVersion));
        DefinitionDigest = definitionDigest ?? throw new ArgumentNullException(nameof(definitionDigest));
    }

    public string LibraryUuid { get; }
    public int SchemaVersion { get; }
    public ExactMetadataDuplicateGroupId GroupId { get; }
    public IReadOnlyList<CalibreBookId> MemberIds { get; }
    public RecommendationModelVersion RecommendationModelVersion { get; }
    public RecommendationInputVersion RecommendationInputVersion { get; }
    public CleanupPlanPolicyVersion PolicyVersion { get; }
    public CleanupPlanContentDigest DefinitionDigest { get; }
}
