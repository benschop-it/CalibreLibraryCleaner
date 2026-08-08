using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public sealed record ContentSignaturePolicyVersion
{
    public static ContentSignaturePolicyVersion V1 { get; } = new("epub-content-signature/1.0.0");
    public static ContentSignaturePolicyVersion V2 { get; } = new("epub-content-signature/1.1.0");
    public static ContentSignaturePolicyVersion Current => V2;

    public ContentSignaturePolicyVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record ContentLandmarkSignature
{
    public ContentLandmarkSignature(
        int ordinal,
        long startToken,
        int tokenCount,
        int distinctTokenCount,
        Sha256Digest strictHash,
        Sha256Digest relaxedHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, 12);
        ArgumentOutOfRangeException.ThrowIfNegative(startToken);
        ArgumentOutOfRangeException.ThrowIfLessThan(tokenCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokenCount, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(distinctTokenCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distinctTokenCount, tokenCount);
        Ordinal = ordinal;
        StartToken = startToken;
        TokenCount = tokenCount;
        DistinctTokenCount = distinctTokenCount;
        StrictHash = strictHash;
        RelaxedHash = relaxedHash;
    }

    public int Ordinal { get; }
    public long StartToken { get; }
    public int TokenCount { get; }
    public int DistinctTokenCount { get; }
    public Sha256Digest StrictHash { get; }
    public Sha256Digest RelaxedHash { get; }
}

public sealed record EpubContentSignature
{
    public EpubContentSignature(
        FormatFileFingerprint fingerprint,
        long totalTokenCount,
        int spineItemCount,
        int sampledChapterCount,
        IEnumerable<ContentLandmarkSignature> landmarks,
        ContentSignaturePolicyVersion? policyVersion = null,
        string? detectedLanguage = null,
        int? languageConfidencePermille = null,
        bool analysisTruncated = false,
        IEnumerable<ulong>? shingleMinHashes = null)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(landmarks);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalTokenCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spineItemCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampledChapterCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampledChapterCount, spineItemCount);
        ContentLandmarkSignature[] ordered = landmarks.OrderBy(value => value.Ordinal).ToArray();
        if (ordered.Length is < 1 or > 12
            || ordered.Select(value => value.Ordinal).Distinct().Count() != ordered.Length
            || ordered.Any(value => value.StartToken + value.TokenCount > totalTokenCount))
            throw new ArgumentException("Content landmarks are invalid.", nameof(landmarks));
        string? language = CandidateMetadataNormalizer.NormalizeLanguage(detectedLanguage);
        if ((language is null) != (languageConfidencePermille is null)
            || languageConfidencePermille is < 0 or > 1000)
            throw new ArgumentException("Detected language evidence is invalid.", nameof(detectedLanguage));
        ulong[] shingleValues = (shingleMinHashes ?? []).Distinct().Order().Take(65).ToArray();
        if (shingleValues.Length > 64)
            throw new ArgumentException("The content shingle sketch exceeds its bound.", nameof(shingleMinHashes));
        Fingerprint = fingerprint;
        TotalTokenCount = totalTokenCount;
        SpineItemCount = spineItemCount;
        SampledChapterCount = sampledChapterCount;
        Landmarks = new ReadOnlyCollection<ContentLandmarkSignature>(ordered);
        PolicyVersion = policyVersion ?? ContentSignaturePolicyVersion.Current;
        DetectedLanguage = language;
        LanguageConfidencePermille = languageConfidencePermille;
        AnalysisTruncated = analysisTruncated;
        ShingleMinHashes = new ReadOnlyCollection<ulong>(shingleValues);
    }

    public FormatFileFingerprint Fingerprint { get; }
    public long TotalTokenCount { get; }
    public int SpineItemCount { get; }
    public int SampledChapterCount { get; }
    public IReadOnlyList<ContentLandmarkSignature> Landmarks { get; }
    public ContentSignaturePolicyVersion PolicyVersion { get; }
    public string? DetectedLanguage { get; }
    public int? LanguageConfidencePermille { get; }
    public bool AnalysisTruncated { get; }
    public IReadOnlyList<ulong> ShingleMinHashes { get; }
}
