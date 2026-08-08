using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Assessments;

public sealed record EpubContentSignatureCacheKey
{
    private const string ResourceProfileVersion = "epub-content-resource/1.0.0";

    private EpubContentSignatureCacheKey(string value, FormatFileFingerprint fingerprint)
    {
        Value = value;
        Fingerprint = fingerprint;
    }

    public string Value { get; }
    public FormatFileFingerprint Fingerprint { get; }

    public static EpubContentSignatureCacheKey Create(EpubContentSignatureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        StringBuilder canonical = new();
        Append(canonical, ResourceProfileVersion);
        Append(canonical, ContentSignaturePolicyVersion.Current.Value);
        Append(canonical, request.Source.Fingerprint.Sha256.Value);
        Append(canonical, request.Source.Fingerprint.SizeInBytes);
        EpubContentSignatureLimits signature = request.SignatureLimits;
        Append(canonical, signature.LandmarkCount);
        Append(canonical, signature.TokensPerLandmark);
        Append(canonical, signature.MaximumTokenCount);
        Append(canonical, signature.MinimumDistinctTokensPerLandmark);
        EpubInspectionLimits inspection = request.Source.Limits;
        Append(canonical, inspection.MaximumFileBytes);
        Append(canonical, inspection.MaximumArchiveEntries);
        Append(canonical, inspection.MaximumDeclaredUncompressedBytes);
        Append(canonical, inspection.MaximumEntryBytes);
        Append(canonical, inspection.MaximumXmlBytes);
        Append(canonical, inspection.MaximumChapterBytes);
        Append(canonical, inspection.MaximumCssBytes);
        Append(canonical, inspection.MaximumCoverBytes);
        Append(canonical, inspection.MaximumCoverHeaderBytes);
        Append(canonical, inspection.MaximumSpineItems);
        Append(canonical, inspection.MaximumLocalReferences);
        Append(canonical, inspection.MaximumEvidencePerRule);
        Append(canonical, inspection.MaximumReadableCharacters);
        Append(canonical, inspection.MaximumCompressionRatio);
        Append(canonical, inspection.MaximumAggregateCompressionRatio);
        Append(canonical, inspection.MaximumHtmlCharacters);
        Append(canonical, inspection.MaximumHtmlNodes);
        Append(canonical, inspection.MaximumHtmlDepth);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        return new(digest, request.Source.Fingerprint);
    }

    private static void Append(StringBuilder target, string value) =>
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');

    private static void Append(StringBuilder target, long value) =>
        Append(target, value.ToString(CultureInfo.InvariantCulture));
}
