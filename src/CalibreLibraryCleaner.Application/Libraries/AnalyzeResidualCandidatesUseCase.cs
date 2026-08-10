using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Libraries;

public enum ResidualCandidateAnalysisPhase
{
    LoadingState,
    ResolvingEpubTargets,
    DetectingExactMetadata,
    DiscoveringExpandedCandidates,
    MergingUnifiedCandidates,
    Publishing,
    Completed,
}

public sealed record ResidualCandidateAnalysisProgress(
    ResidualCandidateAnalysisPhase Phase,
    int Completed,
    int Total,
    string Message);

public sealed record ResidualCandidateAnalysisResult(
    LibraryState? State,
    int ExactMetadataGroupCount,
    int ExpandedGroupCount,
    int UnifiedGroupCount,
    bool ExpandedLimitExceeded,
    string? ErrorCode = null,
    string? Explanation = null)
{
    public bool IsSuccess => State is not null && ErrorCode is null;

    public static ResidualCandidateAnalysisResult Failure(string code, string explanation) =>
        new(null, 0, 0, 0, false, code, explanation);
}

public sealed partial class AnalyzeResidualCandidatesUseCase(
    ILibraryStateSession stateSession,
    ILibraryPathResolver pathResolver,
    IWorkLanguageCandidateDiscoverer discoverer,
    IClock clock,
    LibraryAnalysisOptions options,
    ILogger<AnalyzeResidualCandidatesUseCase>? logger = null)
{
    private readonly ILogger<AnalyzeResidualCandidatesUseCase> _logger = logger
        ?? NullLogger<AnalyzeResidualCandidatesUseCase>.Instance;

    public async Task<ResidualCandidateAnalysisResult> ExecuteAsync(
        string libraryRoot,
        IProgress<ResidualCandidateAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        long started = Stopwatch.GetTimestamp();
        progress?.Report(new(ResidualCandidateAnalysisPhase.LoadingState, 0, 1, "Loading refreshed candidate state"));
        LibraryState? current = stateSession.GetCurrent(libraryRoot);
        if (current is null)
        {
            LibraryStateSessionOutcome loaded = await stateSession.LoadAsync(libraryRoot, cancellationToken)
                .ConfigureAwait(false);
            if (loaded.IsSuccess) current = loaded.State;
        }
        if (current is not
            {
                IsAuthoritative: true,
                IsWorkflowCheckpointCurrent: true,
                WorkflowCheckpoint:
                {
                    Phase: LibraryWorkflowPhase.CandidatePreparationReady,
                    Source: not null,
                },
            })
            return ResidualCandidateAnalysisResult.Failure(
                "CANDIDATE_ANALYSIS.STATE_NOT_READY",
                "A complete post-Exact refresh is required before candidate analysis.");

        LibraryValidationOutcome validation = await pathResolver.ValidateAsync(libraryRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsSuccess
            || !PathsEqual(validation.Location!.LibraryRoot, current.Snapshot.Identity.LibraryRoot))
            return ResidualCandidateAnalysisResult.Failure(
                "CANDIDATE_ANALYSIS.LIBRARY_INVALID",
                validation.Error?.Message ?? "The selected library does not match refreshed state.");

        List<EpubAssessmentTarget> epubTargets = [];
        int totalEpub = current.Snapshot.Books.Sum(book => book.Formats.Count(format => format.Format == "EPUB"));
        int resolved = 0;
        progress?.Report(new(
            ResidualCandidateAnalysisPhase.ResolvingEpubTargets,
            0,
            totalEpub,
            "Resolving residual EPUB targets"));
        foreach (CalibreBook book in current.Snapshot.Books.OrderBy(value => value.Id.Value))
        {
            foreach (BookFormat format in book.Formats.Where(value => value.Format == "EPUB"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (format.FileStatus != FormatFileStatus.Present
                    || format.Fingerprint is null
                    || format.Observation is null
                    || string.IsNullOrWhiteSpace(format.StoredFileName)
                    || string.IsNullOrWhiteSpace(format.ExpectedRelativePath))
                    return ResidualCandidateAnalysisResult.Failure(
                        "CANDIDATE_ANALYSIS.EPUB_STATE_INCOMPLETE",
                        $"Record {book.Id.Value} has incomplete residual EPUB identity.");
                ResolvedFormatPathOutcome path = pathResolver.ResolveFormat(
                    validation.Location, book.RelativeDirectory, format.StoredFileName, format.Format);
                if (!path.IsSuccess || !PathsEqual(path.Path!.RelativePath, format.ExpectedRelativePath))
                    return ResidualCandidateAnalysisResult.Failure(
                        "CANDIDATE_ANALYSIS.EPUB_PATH_CHANGED",
                        $"Record {book.Id.Value} has a changed or unsafe EPUB path.");
                epubTargets.Add(new(
                    book.Id,
                    format.Format,
                    path.Path.RelativePath,
                    path.Path.LibraryRoot,
                    path.Path.FullPath,
                    format.FileStatus,
                    format.Fingerprint,
                    format.Observation));
                progress?.Report(new(
                    ResidualCandidateAnalysisPhase.ResolvingEpubTargets,
                    ++resolved,
                    totalEpub,
                    "Resolving residual EPUB targets"));
            }
        }

        progress?.Report(new(
            ResidualCandidateAnalysisPhase.DetectingExactMetadata,
            0,
            current.Snapshot.Books.Count,
            "Detecting exact metadata candidates"));
        HashSet<CalibreBookId> incompleteAuthors = current.Snapshot.Findings
            .Where(value => value.Code == "AUTHOR_REFERENCE_MISSING" && value.BookId is not null)
            .Select(value => value.BookId!.Value).ToHashSet();
        IReadOnlyList<ExactMetadataDuplicateGroup> metadataGroups = ExactMetadataDuplicateDetector.Detect(
            current.Snapshot.Books.Where(value => !incompleteAuthors.Contains(value.Id)), cancellationToken);
        progress?.Report(new(
            ResidualCandidateAnalysisPhase.DetectingExactMetadata,
            current.Snapshot.Books.Count,
            current.Snapshot.Books.Count,
            "Exact metadata candidates detected"));

        progress?.Report(new(
            ResidualCandidateAnalysisPhase.DiscoveringExpandedCandidates,
            0,
            current.Snapshot.Books.Count,
            "Discovering Expanded candidates"));
        IProgress<WorkLanguageDiscoveryProgress>? discoveryProgress = progress is null
            ? null
            : new DiscoveryProgressAdapter(progress);
        WorkLanguageDiscoveryResult discovery = await discoverer.ExecuteAsync(
            current.Snapshot.Books,
            current.Snapshot.EpubAssessments,
            epubTargets,
            options.MaxContentSignatureConcurrency,
            discoveryProgress,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new(
            ResidualCandidateAnalysisPhase.MergingUnifiedCandidates,
            0,
            metadataGroups.Count + discovery.Groups.Count,
            "Building disjoint unified candidate groups"));
        IReadOnlyList<UnifiedCandidateGroup> unified = UnifiedCandidateMergePolicy.Merge(
            metadataGroups,
            discovery.Groups,
            current.Snapshot.Books,
            current.Snapshot.EpubAssessments,
            current.Snapshot.PdfAssessments,
            cancellationToken);
        List<LibraryFinding> findings = [.. current.Snapshot.Findings];
        if (discovery.LimitExceeded)
            findings.Add(new(
                "MATCHING.CANDIDATE_LIMIT_EXCEEDED",
                FindingSeverity.Warning,
                "Expanded duplicate discovery stopped at its configured candidate limit.",
                "Exact metadata candidates remain available; improve unusually broad metadata before retrying Expanded discovery."));
        DateTimeOffset analyzedAt = clock.GetUtcNow().ToUniversalTime();
        if (analyzedAt < current.ProjectedAtUtc) analyzedAt = current.ProjectedAtUtc;
        LibrarySnapshot snapshot = new(
            current.Snapshot.Identity,
            analyzedAt,
            current.Snapshot.Books,
            findings,
            current.Snapshot.ExactBinaryDuplicateGroups,
            metadataGroups,
            current.Snapshot.EpubAssessments,
            pdfAssessments: current.Snapshot.PdfAssessments,
            workLanguageCandidateGroups: discovery.Groups,
            matchingRunSummary: discovery.Summary,
            unifiedCandidateGroups: unified);
        progress?.Report(new(ResidualCandidateAnalysisPhase.Publishing, 0, 1, "Publishing unified candidate analysis"));
        LibraryStateSessionOutcome published = await stateSession.CompleteCandidateAnalysisAsync(
            snapshot, current.GenerationId, current.Revision, cancellationToken).ConfigureAwait(false);
        if (!published.IsSuccess)
            return ResidualCandidateAnalysisResult.Failure(
                "CANDIDATE_ANALYSIS.PUBLICATION_FAILED",
                published.Explanation ?? "Unified candidate analysis could not be published.");
        progress?.Report(new(ResidualCandidateAnalysisPhase.Completed, 1, 1, "Unified candidate review is ready"));
        LogAnalysisCompleted(
            _logger,
            current.Snapshot.Books.Count,
            metadataGroups.Count,
            discovery.Groups.Count,
            unified.Count,
            discovery.LimitExceeded,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new(
            published.State,
            metadataGroups.Count,
            discovery.Groups.Count,
            unified.Count,
            discovery.LimitExceeded);
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        NormalizePath(first),
        NormalizePath(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizePath(string value) => value.Replace('\\', '/').TrimEnd('/');

    [LoggerMessage(1, LogLevel.Information,
        "Residual candidate analysis completed. Records={RecordCount}, ExactMetadataGroups={ExactMetadataGroups}, ExpandedGroups={ExpandedGroups}, UnifiedGroups={UnifiedGroups}, ExpandedLimitExceeded={ExpandedLimitExceeded}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogAnalysisCompleted(
        ILogger logger,
        int recordCount,
        int exactMetadataGroups,
        int expandedGroups,
        int unifiedGroups,
        bool expandedLimitExceeded,
        long totalMilliseconds);

    private sealed class DiscoveryProgressAdapter(IProgress<ResidualCandidateAnalysisProgress> progress) :
        IProgress<WorkLanguageDiscoveryProgress>
    {
        public void Report(WorkLanguageDiscoveryProgress value) => progress.Report(new(
            ResidualCandidateAnalysisPhase.DiscoveringExpandedCandidates,
            value.Completed,
            value.Total,
            string.IsNullOrWhiteSpace(value.Detail) ? value.Phase.ToString() : value.Detail));
    }
}
