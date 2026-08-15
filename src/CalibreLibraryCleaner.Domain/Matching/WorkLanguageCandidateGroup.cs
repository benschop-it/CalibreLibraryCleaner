using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public enum CandidateEvidenceStrength
{
    Weak,
    Supporting,
    Strong,
    Anchor,
}

public enum WorkLanguageCandidateConfidence
{
    Ambiguous,
    Probable,
    Strong,
}

public enum MatchingEvidenceStatus
{
    Available,
    Unavailable,
    Stale,
}

public enum WorkLanguageCleanupEligibility
{
    ReviewOnly,
    ExplicitKeeperCleanup,
}

public sealed record MatchingPolicyVersion
{
    public static MatchingPolicyVersion V1 { get; } = new("work-language-matching/1.0.0");
    public static MatchingPolicyVersion V2 { get; } = new("work-language-matching/1.1.0");
    public static MatchingPolicyVersion V3 { get; } = new("work-language-matching/1.2.0");
    public static MatchingPolicyVersion V4 { get; } = new("work-language-matching/1.3.0");
    public static MatchingPolicyVersion V5 { get; } = new("work-language-matching/1.4.0");
    public static MatchingPolicyVersion Current => V5;

    public MatchingPolicyVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record CandidateEvidence
{
    public CandidateEvidence(
        string code,
        CandidateEvidenceStrength strength,
        CandidateEvidenceProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 128 || !Enum.IsDefined(strength))
            throw new ArgumentException("Candidate evidence is invalid.");
        Code = code.Trim();
        Strength = strength;
        Provenance = provenance;
    }

    public string Code { get; }
    public CandidateEvidenceStrength Strength { get; }
    public CandidateEvidenceProvenance? Provenance { get; }
}

public sealed record CandidateEvidenceProvenance
{
    public CandidateEvidenceProvenance(string sourceId, string sourceVersion, string resultId)
    {
        SourceId = Bound(sourceId, 64, nameof(sourceId));
        SourceVersion = Bound(sourceVersion, 128, nameof(sourceVersion));
        ResultId = Bound(resultId, 160, nameof(resultId));
    }

    public string SourceId { get; }
    public string SourceVersion { get; }
    public string ResultId { get; }

    private static string Bound(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record CandidateContradiction
{
    public CandidateContradiction(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 128) throw new ArgumentOutOfRangeException(nameof(code));
        Code = code.Trim();
    }

    public string Code { get; }
}

public sealed record ContentComparisonSummary(
    int ComparedPairCount,
    int EquivalentPairCount,
    int HighSimilarityPairCount,
    int AmbiguousPairCount,
    int DifferentPairCount,
    int UnavailablePairCount)
{
    public ContentComparisonSummary Validate()
    {
        int[] values =
        [
            ComparedPairCount,
            EquivalentPairCount,
            HighSimilarityPairCount,
            AmbiguousPairCount,
            DifferentPairCount,
            UnavailablePairCount,
        ];
        if (values.Any(value => value < 0)
            || values.Skip(1).Sum() != ComparedPairCount)
            throw new ArgumentException("Content comparison counts are inconsistent.");
        return this;
    }
}

public readonly record struct WorkLanguageCandidateGroupId
{
    public WorkLanguageCandidateGroupId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 160) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record WorkLanguageCandidateGroup
{
    public WorkLanguageCandidateGroup(
        WorkLanguageCandidateGroupId id,
        string language,
        IEnumerable<CalibreBookId> members,
        IEnumerable<CalibreBookId> anchorMembers,
        WorkLanguageCandidateConfidence confidence,
        IEnumerable<CandidateEvidence> evidence,
        IEnumerable<CandidateContradiction>? contradictions,
        ContentComparisonSummary contentComparison,
        MatchingPolicyVersion policyVersion,
        MatchingEvidenceStatus evidenceStatus = MatchingEvidenceStatus.Available)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(anchorMembers);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(contentComparison);
        ArgumentNullException.ThrowIfNull(policyVersion);
        CalibreBookId[] orderedMembers = members.Distinct().OrderBy(value => value.Value).ToArray();
        CalibreBookId[] orderedAnchors = anchorMembers.Distinct().OrderBy(value => value.Value).ToArray();
        CandidateEvidence[] orderedEvidence = evidence.Distinct()
            .OrderByDescending(value => value.Strength)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Provenance?.SourceId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.Provenance?.SourceVersion ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.Provenance?.ResultId ?? string.Empty, StringComparer.Ordinal).ToArray();
        CandidateContradiction[] orderedContradictions = (contradictions ?? []).Distinct()
            .OrderBy(value => value.Code, StringComparer.Ordinal).ToArray();
        if (orderedMembers.Length < 2
            || orderedAnchors.Length == 0
            || orderedAnchors.Any(value => !orderedMembers.Contains(value))
            || orderedEvidence.Length == 0
            || !Enum.IsDefined(confidence)
            || !Enum.IsDefined(evidenceStatus))
            throw new ArgumentException("A work-language candidate group is invalid.");
        string normalizedLanguage = language.Trim().ToLowerInvariant();
        WorkLanguageCandidateGroupId expectedId = CreateId(policyVersion, normalizedLanguage, orderedMembers);
        if (id != expectedId) throw new ArgumentException("The candidate group ID is not canonical.", nameof(id));

        Id = id;
        Language = normalizedLanguage;
        Members = new ReadOnlyCollection<CalibreBookId>(orderedMembers);
        AnchorMembers = new ReadOnlyCollection<CalibreBookId>(orderedAnchors);
        Confidence = confidence;
        Evidence = new ReadOnlyCollection<CandidateEvidence>(orderedEvidence);
        Contradictions = new ReadOnlyCollection<CandidateContradiction>(orderedContradictions);
        ContentComparison = contentComparison.Validate();
        PolicyVersion = policyVersion;
        EvidenceStatus = evidenceStatus;
        CleanupEligibility = policyVersion is { } version
            && (version == MatchingPolicyVersion.V3
                || version == MatchingPolicyVersion.V4
                || version == MatchingPolicyVersion.V5)
            && ContentComparison.ComparedPairCount > 0
            && ContentComparison.EquivalentPairCount + ContentComparison.HighSimilarityPairCount
                == ContentComparison.ComparedPairCount
            ? WorkLanguageCleanupEligibility.ExplicitKeeperCleanup
            : WorkLanguageCleanupEligibility.ReviewOnly;
    }

    public WorkLanguageCandidateGroupId Id { get; }
    public string Language { get; }
    public IReadOnlyList<CalibreBookId> Members { get; }
    public IReadOnlyList<CalibreBookId> AnchorMembers { get; }
    public WorkLanguageCandidateConfidence Confidence { get; }
    public IReadOnlyList<CandidateEvidence> Evidence { get; }
    public IReadOnlyList<CandidateContradiction> Contradictions { get; }
    public ContentComparisonSummary ContentComparison { get; }
    public MatchingPolicyVersion PolicyVersion { get; }
    public MatchingEvidenceStatus EvidenceStatus { get; }
    public WorkLanguageCleanupEligibility CleanupEligibility { get; }

    public static WorkLanguageCandidateGroup Create(
        string language,
        IEnumerable<CalibreBookId> members,
        IEnumerable<CalibreBookId> anchorMembers,
        WorkLanguageCandidateConfidence confidence,
        IEnumerable<CandidateEvidence> evidence,
        IEnumerable<CandidateContradiction>? contradictions = null,
        ContentComparisonSummary? contentComparison = null,
        MatchingPolicyVersion? policyVersion = null)
    {
        MatchingPolicyVersion version = policyVersion ?? MatchingPolicyVersion.Current;
        CalibreBookId[] orderedMembers = members.Distinct().OrderBy(value => value.Value).ToArray();
        string normalizedLanguage = language.Trim().ToLowerInvariant();
        return new(CreateId(version, normalizedLanguage, orderedMembers), normalizedLanguage,
            orderedMembers, anchorMembers, confidence, evidence, contradictions,
            contentComparison ?? new(0, 0, 0, 0, 0, 0), version);
    }

    private static WorkLanguageCandidateGroupId CreateId(
        MatchingPolicyVersion version,
        string language,
        IEnumerable<CalibreBookId> members)
    {
        StringBuilder canonical = new();
        Append(canonical, version.Value);
        Append(canonical, language);
        foreach (CalibreBookId member in members.OrderBy(value => value.Value))
            Append(canonical, member.Value.ToString(CultureInfo.InvariantCulture));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        return new($"work-language:v1:{digest}");
    }

    private static void Append(StringBuilder target, string value) =>
        target.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('|');
}

public sealed record BookMatchingRunSummary
{
    public BookMatchingRunSummary(
        MatchingPolicyVersion policyVersion,
        MatchingEvidenceStatus status,
        int recordCount,
        int proposedPairCount,
        int retainedPairCount,
        int recordsCapped,
        int contentSignaturesRequested,
        int contentCacheHits,
        int contentComparisons,
        int inferredGroupCount,
        int unknownLanguageCount,
        int bibliographicQueries = 0,
        int bibliographicCacheHits = 0,
        int bibliographicProviderRequests = 0,
        int bibliographicMatchedRecords = 0,
        int bibliographicFailures = 0,
        bool bibliographicRequestLimitReached = false,
        bool bibliographicEnabled = false,
        int localEmbeddingPairs = 0,
        int localEmbeddingCacheHits = 0,
        int localEmbeddingModelBatches = 0,
        int localEmbeddingComparisons = 0,
        int localEmbeddingFailures = 0,
        bool localEmbeddingInputLimitReached = false,
        bool localEmbeddingEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(policyVersion);
        int[] counts =
        [
            recordCount,
            proposedPairCount,
            retainedPairCount,
            recordsCapped,
            contentSignaturesRequested,
            contentCacheHits,
            contentComparisons,
            inferredGroupCount,
            unknownLanguageCount,
            bibliographicQueries,
            bibliographicCacheHits,
            bibliographicProviderRequests,
            bibliographicMatchedRecords,
            bibliographicFailures,
            localEmbeddingPairs,
            localEmbeddingCacheHits,
            localEmbeddingModelBatches,
            localEmbeddingComparisons,
            localEmbeddingFailures,
        ];
        if (!Enum.IsDefined(status) || counts.Any(value => value < 0)
            || retainedPairCount > proposedPairCount
            || contentCacheHits > contentSignaturesRequested
            || unknownLanguageCount > recordCount
            || bibliographicCacheHits > bibliographicQueries
            || bibliographicProviderRequests > bibliographicQueries
            || bibliographicMatchedRecords > recordCount
            || bibliographicFailures > bibliographicProviderRequests
            || localEmbeddingCacheHits > localEmbeddingPairs
            || localEmbeddingComparisons > localEmbeddingPairs
            || localEmbeddingFailures > recordCount)
            throw new ArgumentException("The matching run summary is invalid.");
        PolicyVersion = policyVersion;
        Status = status;
        RecordCount = recordCount;
        ProposedPairCount = proposedPairCount;
        RetainedPairCount = retainedPairCount;
        RecordsCapped = recordsCapped;
        ContentSignaturesRequested = contentSignaturesRequested;
        ContentCacheHits = contentCacheHits;
        ContentComparisons = contentComparisons;
        InferredGroupCount = inferredGroupCount;
        UnknownLanguageCount = unknownLanguageCount;
        BibliographicQueries = bibliographicQueries;
        BibliographicCacheHits = bibliographicCacheHits;
        BibliographicProviderRequests = bibliographicProviderRequests;
        BibliographicMatchedRecords = bibliographicMatchedRecords;
        BibliographicFailures = bibliographicFailures;
        BibliographicRequestLimitReached = bibliographicRequestLimitReached;
        BibliographicEnabled = bibliographicEnabled;
        LocalEmbeddingPairs = localEmbeddingPairs;
        LocalEmbeddingCacheHits = localEmbeddingCacheHits;
        LocalEmbeddingModelBatches = localEmbeddingModelBatches;
        LocalEmbeddingComparisons = localEmbeddingComparisons;
        LocalEmbeddingFailures = localEmbeddingFailures;
        LocalEmbeddingInputLimitReached = localEmbeddingInputLimitReached;
        LocalEmbeddingEnabled = localEmbeddingEnabled;
    }

    public MatchingPolicyVersion PolicyVersion { get; }
    public MatchingEvidenceStatus Status { get; }
    public int RecordCount { get; }
    public int ProposedPairCount { get; }
    public int RetainedPairCount { get; }
    public int RecordsCapped { get; }
    public int ContentSignaturesRequested { get; }
    public int ContentCacheHits { get; }
    public int ContentComparisons { get; }
    public int InferredGroupCount { get; }
    public int UnknownLanguageCount { get; }
    public int BibliographicQueries { get; }
    public int BibliographicCacheHits { get; }
    public int BibliographicProviderRequests { get; }
    public int BibliographicMatchedRecords { get; }
    public int BibliographicFailures { get; }
    public bool BibliographicRequestLimitReached { get; }
    public bool BibliographicEnabled { get; }
    public int LocalEmbeddingPairs { get; }
    public int LocalEmbeddingCacheHits { get; }
    public int LocalEmbeddingModelBatches { get; }
    public int LocalEmbeddingComparisons { get; }
    public int LocalEmbeddingFailures { get; }
    public bool LocalEmbeddingInputLimitReached { get; }
    public bool LocalEmbeddingEnabled { get; }

    public static BookMatchingRunSummary Unavailable(int recordCount) => new(
        MatchingPolicyVersion.Current,
        MatchingEvidenceStatus.Unavailable,
        recordCount,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        recordCount);
}
