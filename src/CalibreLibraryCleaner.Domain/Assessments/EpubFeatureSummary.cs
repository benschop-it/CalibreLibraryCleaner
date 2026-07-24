using System.Collections.ObjectModel;

namespace CalibreLibraryCleaner.Domain.Assessments;

public sealed record EpubFeatureSummary
{
    public EpubFeatureSummary(
        bool opened,
        bool packageParsed,
        string? packageVersion = null,
        string? embeddedTitle = null,
        IEnumerable<string>? authors = null,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? dates = null,
        IEnumerable<string>? strongIdentifiers = null,
        bool coverPresent = false,
        int? coverWidth = null,
        int? coverHeight = null,
        bool navigationPresent = false,
        int manifestItemCount = 0,
        int spineItemCount = 0,
        int chapterCount = 0,
        int localResourceCount = 0,
        int brokenReferenceCount = 0,
        int readableCharacterCount = 0,
        string encryptionState = "None",
        bool analysisTruncated = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(manifestItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(spineItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(chapterCount);
        ArgumentOutOfRangeException.ThrowIfNegative(localResourceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(brokenReferenceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(readableCharacterCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionState);
        if ((coverWidth is null) != (coverHeight is null) || coverWidth <= 0 || coverHeight <= 0)
        {
            throw new ArgumentException("Cover dimensions must be absent or a positive width-height pair.", nameof(coverWidth));
        }

        Opened = opened;
        PackageParsed = packageParsed;
        PackageVersion = Bound(packageVersion);
        EmbeddedTitle = Bound(embeddedTitle);
        Authors = CopyBounded(authors);
        Languages = CopyBounded(languages);
        Dates = CopyBounded(dates);
        StrongIdentifiers = CopyBounded(strongIdentifiers);
        CoverPresent = coverPresent;
        CoverWidth = coverWidth;
        CoverHeight = coverHeight;
        NavigationPresent = navigationPresent;
        ManifestItemCount = manifestItemCount;
        SpineItemCount = spineItemCount;
        ChapterCount = chapterCount;
        LocalResourceCount = localResourceCount;
        BrokenReferenceCount = brokenReferenceCount;
        ReadableCharacterCount = readableCharacterCount;
        EncryptionState = encryptionState;
        AnalysisTruncated = analysisTruncated;
    }

    public bool Opened { get; }
    public bool PackageParsed { get; }
    public string? PackageVersion { get; }
    public string? EmbeddedTitle { get; }
    public IReadOnlyList<string> Authors { get; }
    public IReadOnlyList<string> Languages { get; }
    public IReadOnlyList<string> Dates { get; }
    public IReadOnlyList<string> StrongIdentifiers { get; }
    public bool CoverPresent { get; }
    public int? CoverWidth { get; }
    public int? CoverHeight { get; }
    public bool NavigationPresent { get; }
    public int ManifestItemCount { get; }
    public int SpineItemCount { get; }
    public int ChapterCount { get; }
    public int LocalResourceCount { get; }
    public int BrokenReferenceCount { get; }
    public int ReadableCharacterCount { get; }
    public string EncryptionState { get; }
    public bool AnalysisTruncated { get; }

    private static string? Bound(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 512)];

    private static ReadOnlyCollection<string> CopyBounded(IEnumerable<string>? values) => new(
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Bound(value)!).Take(100).ToArray());
}
