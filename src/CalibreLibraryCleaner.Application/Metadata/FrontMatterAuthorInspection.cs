using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record FrontMatterInspectionLimits
{
    public static FrontMatterInspectionLimits V1 { get; } = new(3, 5, 32_768);

    public FrontMatterInspectionLimits(
        int maximumEpubDocuments,
        int maximumPdfPages,
        int maximumUsefulCharactersPerUnit)
    {
        if (maximumEpubDocuments is < 1 or > 3
            || maximumPdfPages is < 1 or > 5
            || maximumUsefulCharactersPerUnit is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(maximumEpubDocuments));
        MaximumEpubDocuments = maximumEpubDocuments;
        MaximumPdfPages = maximumPdfPages;
        MaximumUsefulCharactersPerUnit = maximumUsefulCharactersPerUnit;
    }

    public int MaximumEpubDocuments { get; }
    public int MaximumPdfPages { get; }
    public int MaximumUsefulCharactersPerUnit { get; }
}

public static class FrontMatterAuthorVersions
{
    public const string EpubExtractor = "epub-front-matter-identity/2.0.0+"
        + FrontMatterIdentityExtraction.PolicyVersion + "+" + AuthorBylinePolicy.PolicyVersion;
    public const string PdfExtractor = "pdf-front-matter-identity/2.0.0+"
        + FrontMatterIdentityExtraction.PolicyVersion + "+" + AuthorBylinePolicy.PolicyVersion;
    public const string Limits = "front-matter-author-limits/1.0.0";
}

public sealed record FrontMatterAuthorCacheKey
{
    public FrontMatterAuthorCacheKey(
        string format,
        FormatFileFingerprint fingerprint,
        string extractorVersion,
        string limitsVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractorVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(limitsVersion);
        string canonical = format.Trim().ToUpperInvariant();
        if (canonical is not ("EPUB" or "PDF")
            || extractorVersion.Length > 128
            || limitsVersion.Length > 128)
            throw new ArgumentException("Front-matter author cache identity is invalid.");
        Format = canonical;
        Fingerprint = fingerprint;
        ExtractorVersion = extractorVersion.Trim();
        LimitsVersion = limitsVersion.Trim();
    }

    public string Format { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public string ExtractorVersion { get; }
    public string LimitsVersion { get; }
}

public enum FrontMatterAuthorStatus
{
    Available,
    Missing,
    Unavailable,
}

public sealed record FrontMatterAuthorCandidate
{
    public FrontMatterAuthorCandidate(
        AuthorCreditRole role,
        IEnumerable<string> names,
        int unitNumber,
        int lineNumber,
        string marker)
    {
        AuthorBylineCandidate value = new(role, names, marker, lineNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitNumber);
        Role = value.Role;
        Names = value.Names;
        UnitNumber = unitNumber;
        LineNumber = value.LineNumber;
        Marker = value.Marker;
    }

    public AuthorCreditRole Role { get; }
    public IReadOnlyList<string> Names { get; }
    public int UnitNumber { get; }
    public int LineNumber { get; }
    public string Marker { get; }
}

public sealed record FrontMatterAuthorObservation
{
    public FrontMatterAuthorObservation(
        string format,
        FormatFileFingerprint fingerprint,
        string extractorVersion,
        string limitsVersion,
        FrontMatterAuthorStatus status,
        IEnumerable<FrontMatterAuthorCandidate> candidates,
        int requestedUnits,
        int completedUnits,
        int retainedUsefulCharacters,
        bool truncated,
        string? problemCode = null,
        IEnumerable<FrontMatterIdentityCandidate>? identityCandidates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractorVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(limitsVersion);
        ArgumentNullException.ThrowIfNull(candidates);
        string canonicalFormat = format.Trim().ToUpperInvariant();
        FrontMatterAuthorCandidate[] values = candidates.Take(32).ToArray();
        FrontMatterIdentityCandidate[] identities = (identityCandidates ?? []).Take(17).ToArray();
        string? problem = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
        if (canonicalFormat is not ("EPUB" or "PDF")
            || !Enum.IsDefined(status)
            || requestedUnits < 0
            || completedUnits < 0
            || completedUnits > requestedUnits
            || retainedUsefulCharacters < 0
            || requestedUnits > (canonicalFormat == "EPUB" ? 3 : 5)
            || retainedUsefulCharacters > completedUnits * 32_768
            || identities.Length > 16
            || identities.Any(value => value.UnitNumber > requestedUnits)
            || status == FrontMatterAuthorStatus.Unavailable && identities.Length > 0
            || extractorVersion.Length > 128
            || limitsVersion.Length > 128
            || problem is { Length: > 128 }
            || status == FrontMatterAuthorStatus.Available
                && values.All(value => value.Role != AuthorCreditRole.Author)
            || status != FrontMatterAuthorStatus.Available
                && values.Any(value => value.Role == AuthorCreditRole.Author)
            || status == FrontMatterAuthorStatus.Unavailable && problem is null
            || status != FrontMatterAuthorStatus.Unavailable && problem is not null)
            throw new ArgumentException("Front-matter author observation is invalid.");
        Format = canonicalFormat;
        Fingerprint = fingerprint;
        ExtractorVersion = extractorVersion.Trim();
        LimitsVersion = limitsVersion.Trim();
        Status = status;
        Candidates = new ReadOnlyCollection<FrontMatterAuthorCandidate>(values);
        RequestedUnits = requestedUnits;
        CompletedUnits = completedUnits;
        RetainedUsefulCharacters = retainedUsefulCharacters;
        Truncated = truncated;
        ProblemCode = problem;
        IdentityCandidates = new ReadOnlyCollection<FrontMatterIdentityCandidate>(identities);
    }

    public string Format { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public string ExtractorVersion { get; }
    public string LimitsVersion { get; }
    public FrontMatterAuthorStatus Status { get; }
    public IReadOnlyList<FrontMatterAuthorCandidate> Candidates { get; }
    public int RequestedUnits { get; }
    public int CompletedUnits { get; }
    public int RetainedUsefulCharacters { get; }
    public bool Truncated { get; }
    public string? ProblemCode { get; }
    public IReadOnlyList<FrontMatterIdentityCandidate> IdentityCandidates { get; }
    public FinalRecordRouteOutcome Coverage => Status == FrontMatterAuthorStatus.Unavailable
        ? FinalRecordRouteOutcome.Unavailable
        : Truncated || CompletedUnits < RequestedUnits || RequestedUnits == 0
            ? FinalRecordRouteOutcome.DeferredByBounds
            : Candidates.Count > 0 || IdentityCandidates.Count > 0
                ? FinalRecordRouteOutcome.CompletedWithEvidence
                : RetainedUsefulCharacters == 0
                    ? FinalRecordRouteOutcome.Unavailable
                    : FinalRecordRouteOutcome.CompletedWithoutEvidence;
}

public interface IFrontMatterAuthorCache
{
    Task<FrontMatterAuthorObservation?> TryReadAsync(
        FrontMatterAuthorCacheKey key,
        CancellationToken cancellationToken);

    Task WriteAsync(
        FrontMatterAuthorObservation observation,
        CancellationToken cancellationToken);
}
