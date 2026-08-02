using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
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
    int MaximumHtmlNodes = 200_000,
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

public sealed record EpubInspectionProblem(
    EpubInspectionProblemCode Code,
    string Explanation,
    string? Evidence = null,
    EpubInspectionIssueCode? IssueCode = null,
    bool AllowsFallbackInspection = true);

public enum EpubInspectionIssueCode
{
    InvalidManifestItemPath,
    InvalidManifestItemName,
    MalformedNavigation,
    MissingNavigationMap,
    MissingContainer,
    MalformedContainer,
    MissingPackage,
    MalformedPackage,
    UnsupportedParser,
    UnsafeEntry,
    DuplicateEntry,
    EncryptedEntry,
    UnsupportedEntry,
    SuspiciousEntry,
    OversizedEntry,
    UnknownReadingOrder,
    PartialCoverage,
}

public sealed record EpubInspectionIssue
{
    public EpubInspectionIssue(
        EpubInspectionIssueCode code,
        string stage,
        string? item = null,
        long? observed = null,
        long? limit = null,
        int omittedCount = 0,
        string? exceptionType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentOutOfRangeException.ThrowIfNegative(omittedCount);
        if (observed < 0 || limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observed), "Observed values and limits cannot be negative.");
        }

        Code = code;
        Stage = Bound(stage)!;
        Item = Bound(item);
        Observed = observed;
        Limit = limit;
        OmittedCount = omittedCount;
        ExceptionType = Bound(exceptionType);
    }

    public EpubInspectionIssueCode Code { get; }
    public string Stage { get; }
    public string? Item { get; }
    public long? Observed { get; }
    public long? Limit { get; }
    public int OmittedCount { get; }
    public string? ExceptionType { get; }

    private static string? Bound(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(value.Trim().Length, 512)];
}

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
    int? TotalRemoteReferences = null,
    IReadOnlyList<EpubInspectionProblem>? RecoverableProblems = null,
    EpubAssessmentCoverage Coverage = EpubAssessmentCoverage.Full,
    EpubAssessmentFacet AvailableFacets = EpubAssessmentFacet.All,
    IReadOnlyList<EpubInspectionIssue>? Issues = null,
    int FallbackCandidateCount = 0,
    int FallbackRenderableCount = 0,
    EpubRenderableEvidence RenderableEvidence = EpubRenderableEvidence.None)
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
        [new(code, explanation)],
        Coverage: EpubAssessmentCoverage.Incomplete,
        AvailableFacets: EpubAssessmentFacet.None);
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
