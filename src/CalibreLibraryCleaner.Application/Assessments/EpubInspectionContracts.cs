using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Assessments;

public sealed record EpubInspectionLimits(
    long MaximumFileBytes = 1024L * 1024 * 1024,
    int MaximumArchiveEntries = 10_000,
    long MaximumDeclaredUncompressedBytes = 512L * 1024 * 1024,
    long MaximumEntryBytes = 64L * 1024 * 1024,
    long MaximumXmlBytes = 4L * 1024 * 1024,
    long MaximumChapterBytes = 8L * 1024 * 1024,
    long MaximumCssBytes = 2L * 1024 * 1024,
    long MaximumCoverBytes = 32L * 1024 * 1024,
    int MaximumCoverHeaderBytes = 64 * 1024,
    int MaximumSpineItems = 10_000,
    int MaximumLocalReferences = 50_000,
    int MaximumEvidencePerRule = 100,
    int MaximumReadableCharacters = 20_000_000,
    int MaximumCompressionRatio = 200,
    int MaximumAggregateCompressionRatio = 100,
    int MaximumHtmlNodes = 25_000,
    int MaximumHtmlDepth = 256)
{
    public static EpubInspectionLimits V1 { get; } = new();
}

public sealed record EpubInspectionRequest(
    CalibreBookId BookId,
    string LibraryRoot,
    string FullPath,
    string ExpectedRelativePath,
    FormatFileFingerprint Fingerprint,
    FormatFileObservation Observation,
    EpubInspectionLimits Limits);

public sealed record EpubInspectionProgress(string Stage, int CompletedUnits, int? TotalUnits);

public enum EpubInspectionProblemCode
{
    CannotOpen,
    UnsafeArchive,
    PackageMalformed,
    Unsupported,
    Encrypted,
    ChangedDuringInspection,
    LimitExceeded,
    Unreadable,
}

public sealed record EpubInspectionProblem(EpubInspectionProblemCode Code, string Explanation, string? Evidence = null);

public sealed record EpubInspectionResult(
    CalibreBookId BookId,
    string ExpectedRelativePath,
    bool Opened,
    bool ArchiveSafe,
    bool PackageParsed,
    string? PackageVersion,
    string? EmbeddedTitle,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Dates,
    IReadOnlyList<string> StrongIdentifiers,
    bool CoverPresent,
    int? CoverWidth,
    int? CoverHeight,
    bool NavigationPresent,
    int ManifestItemCount,
    int SpineItemCount,
    int ChapterCount,
    int LocalResourceCount,
    IReadOnlyList<string> MissingSpineResources,
    IReadOnlyList<string> BrokenInternalReferences,
    IReadOnlyList<string> EmptyChapters,
    IReadOnlyList<string> RepeatedReferences,
    IReadOnlyList<string> RemoteReferences,
    int ReadableCharacterCount,
    string EncryptionState,
    bool AnalysisTruncated,
    IReadOnlyList<EpubInspectionProblem> Problems,
    bool CoverHeaderMalformed = false,
    IReadOnlyList<string>? OptionalTruncations = null,
    int? TotalMissingSpineResources = null,
    int? TotalBrokenInternalReferences = null,
    int? TotalEmptyChapters = null,
    int? TotalRepeatedReferences = null,
    int? TotalRemoteReferences = null)
{
    public static EpubInspectionResult Failed(
        CalibreBookId bookId,
        string expectedRelativePath,
        EpubInspectionProblemCode code,
        string explanation) => new(
        bookId,
        expectedRelativePath,
        false,
        false,
        false,
        null,
        null,
        [],
        [],
        [],
        [],
        false,
        null,
        null,
        false,
        0,
        0,
        0,
        0,
        [],
        [],
        [],
        [],
        [],
        0,
        "Unknown",
        false,
        [new(code, explanation)]);
}

public sealed record EpubAssessmentTarget(
    CalibreBookId BookId,
    string Format,
    string ExpectedRelativePath,
    string? LibraryRoot,
    string? FullPath,
    FormatFileStatus FileStatus,
    FormatFileFingerprint? Fingerprint,
    FormatFileObservation? Observation);

public sealed record EpubAssessmentProgress(int CompletedFiles, int TotalFiles, string CurrentRelativePath, string Stage);
