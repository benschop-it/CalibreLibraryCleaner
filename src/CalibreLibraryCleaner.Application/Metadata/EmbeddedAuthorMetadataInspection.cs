using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Metadata;

public static class EmbeddedAuthorMetadataVersions
{
    public const string EpubExtractor = "epub-author-metadata/1.0.0";
    public const string EpubLimits = "epub-author-metadata-limits/1.0.0";
    public const string PdfExtractor = "pdf-author-metadata/1.0.0";
    public const string PdfLimits = "pdf-author-metadata-limits/1.0.0";
}

public enum EmbeddedAuthorMetadataStatus
{
    Available,
    Missing,
    Unavailable,
}

public sealed record EmbeddedAuthorMetadataCacheKey
{
    public EmbeddedAuthorMetadataCacheKey(
        string format,
        FormatFileFingerprint fingerprint,
        string extractorVersion,
        string limitsVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractorVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(limitsVersion);
        string canonicalFormat = format.Trim().ToUpperInvariant();
        if (canonicalFormat is not ("EPUB" or "PDF")
            || extractorVersion.Length > 128
            || limitsVersion.Length > 128
            || extractorVersion.Any(char.IsControl)
            || limitsVersion.Any(char.IsControl))
            throw new ArgumentException("Embedded author metadata cache identity is invalid.");
        Format = canonicalFormat;
        Fingerprint = fingerprint;
        ExtractorVersion = extractorVersion.Trim();
        LimitsVersion = limitsVersion.Trim();
    }

    public string Format { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public string ExtractorVersion { get; }
    public string LimitsVersion { get; }
}

public sealed record EmbeddedAuthorMetadataObservation
{
    public EmbeddedAuthorMetadataObservation(
        EmbeddedAuthorMetadataCacheKey key,
        EmbeddedAuthorMetadataStatus status,
        string? embeddedTitle,
        IEnumerable<string>? authors,
        string? problemCode = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        string? title = Bound(embeddedTitle);
        string[] values = (authors ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value)!)
            .Take(32)
            .ToArray();
        string? problem = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
        if (problem is { Length: > 128 }
            || problem is not null && problem.Any(char.IsControl)
            || status == EmbeddedAuthorMetadataStatus.Available && values.Length == 0
            || status != EmbeddedAuthorMetadataStatus.Available && values.Length != 0
            || status == EmbeddedAuthorMetadataStatus.Unavailable && problem is null
            || status != EmbeddedAuthorMetadataStatus.Unavailable && problem is not null)
            throw new ArgumentException("Embedded author metadata observation is invalid.");
        Key = key;
        Status = status;
        EmbeddedTitle = title;
        Authors = new ReadOnlyCollection<string>(values);
        ProblemCode = problem;
    }

    public EmbeddedAuthorMetadataCacheKey Key { get; }
    public EmbeddedAuthorMetadataStatus Status { get; }
    public string? EmbeddedTitle { get; }
    public IReadOnlyList<string> Authors { get; }
    public string? ProblemCode { get; }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        return normalized[..Math.Min(normalized.Length, 512)];
    }
}

public interface IEmbeddedAuthorMetadataCache
{
    Task<EmbeddedAuthorMetadataObservation?> TryReadAsync(
        EmbeddedAuthorMetadataCacheKey key,
        CancellationToken cancellationToken);

    Task WriteAsync(
        EmbeddedAuthorMetadataObservation observation,
        CancellationToken cancellationToken);
}
