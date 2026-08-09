using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Recommendations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed partial class ScanLibraryUseCase(
    ILibraryPathResolver pathResolver,
    ICalibreMetadataReader metadataReader,
    IFormatFileHasher formatFileHasher,
    IClock clock,
    LibraryAnalysisOptions options,
    AssessEpubFormatsUseCase? assessEpubFormats = null,
    GenerateConsolidationRecommendationsUseCase? generateRecommendations = null,
    AssessPdfFormatsUseCase? assessPdfFormats = null,
    DiscoverWorkLanguageCandidatesUseCase? discoverWorkLanguageCandidates = null,
    ILibraryStateSession? libraryStateSession = null,
    ILogger<ScanLibraryUseCase>? logger = null)
{
    private readonly ILogger<ScanLibraryUseCase> _logger = logger
        ?? NullLogger<ScanLibraryUseCase>.Instance;

    public async Task<LibraryScanOutcome> ExecuteAsync(
        string? candidatePath,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken,
        bool includePdfAssessments = true)
    {
        long scanStarted = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(LibraryScanPhase.Validating, 0, 1, "Validating library"));
        LibraryValidationOutcome validation = await pathResolver
            .ValidateAsync(candidatePath, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return LibraryScanOutcome.Failure(validation.Error!);
        }

        ValidatedLibraryLocation validatedLocation = validation.Location!;
        LibraryState? previousState = libraryStateSession?.GetCurrent(validatedLocation.LibraryRoot);
        if (previousState is null && libraryStateSession is not null)
        {
            LibraryStateSessionOutcome load = await libraryStateSession.LoadAsync(
                validatedLocation.LibraryRoot, cancellationToken).ConfigureAwait(false);
            if (load.IsSuccess) previousState = load.State;
        }
        LibrarySnapshot? previousSnapshot = previousState?.IsAuthoritative == true
            ? previousState.Snapshot
            : null;
        progress?.Report(new(LibraryScanPhase.Validating, 1, 1, "Library validated"));
        CalibreCatalogReadOutcome readOutcome = await metadataReader
            .ReadAsync(validation.Location!, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!readOutcome.IsSuccess)
        {
            return LibraryScanOutcome.Failure(readOutcome.Error!);
        }

        CalibreCatalogRecord catalog = readOutcome.Catalog!;
        long catalogReadCompleted = Stopwatch.GetTimestamp();
        List<LibraryFinding> findings = catalog.Issues
            .OrderBy(issue => issue.BookId)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(MapIssue)
            .ToList();
        List<PreparedBook> preparedBooks = [];
        List<FormatHashRequest> requests = [];
        long totalFormats = catalog.Books.Sum(book => (long)book.Formats.Count);
        long completedFormats = 0;
        progress?.Report(new(LibraryScanPhase.ResolvingFiles, 0, totalFormats, "Resolving format files"));

        foreach (CalibreBookRecord bookRecord in catalog.Books.OrderBy(book => book.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibreBookId bookId = new(bookRecord.Id);
            List<PreparedFormat> formats = [];
            foreach (CalibreFormatRecord formatRecord in bookRecord.Formats
                         .OrderBy(format => format.Format, StringComparer.Ordinal)
                         .ThenBy(format => format.StoredName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string canonicalFormat = formatRecord.Format.ToUpperInvariant();
                ResolvedFormatPathOutcome resolved = pathResolver.ResolveFormat(
                    validation.Location!,
                    bookRecord.RelativeDirectory,
                    formatRecord.StoredName,
                    canonicalFormat);
                if (!resolved.IsSuccess)
                {
                    formats.Add(new(canonicalFormat, formatRecord.StoredName, string.Empty, null));
                    findings.Add(CreateInvalidPathFinding(
                        bookId,
                        canonicalFormat,
                        bookRecord.RelativeDirectory,
                        resolved.Reason!));
                }
                else
                {
                    int sequence = requests.Count;
                    requests.Add(new(sequence, bookId, canonicalFormat, resolved.Path!));
                    formats.Add(new(
                        canonicalFormat,
                        formatRecord.StoredName,
                        resolved.Path!.RelativePath,
                        sequence));
                }

                completedFormats++;
                ReportProgress(progress, LibraryScanPhase.ResolvingFiles, completedFormats, totalFormats, "Resolving format files");
            }

            preparedBooks.Add(new(bookRecord, formats));
        }
        long resolutionCompleted = Stopwatch.GetTimestamp();

        IReadOnlyList<FormatHashResult> hashResults;
        try
        {
            IProgress<FormatHashProgress>? hashProgress = progress is null
                ? null
                : new ProgressAdapter(progress);
            hashResults = await formatFileHasher.HashAsync(
                    requests,
                    options.MaxHashConcurrency,
                    hashProgress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LibraryScanOutcome.Failure(new(
                LibraryErrorCode.HashingFailed,
                "The ebook files could not be hashed reliably.",
                "Close tools changing the library and retry the scan."));
        }

        if (!AreHashResultsValid(requests, hashResults))
        {
            return LibraryScanOutcome.Failure(new(
                LibraryErrorCode.HashingFailed,
                "The ebook hashing results were incomplete.",
                "Retry the scan. If the problem continues, inspect the application log."));
        }
        long hashingCompleted = Stopwatch.GetTimestamp();

        Dictionary<int, FormatHashResult> resultsBySequence = hashResults.ToDictionary(result => result.Sequence);
        List<CalibreBook> books = preparedBooks
            .Select(book => MapBook(book, resultsBySequence, findings, cancellationToken))
            .ToList();
        List<EpubAssessmentTarget> epubTargets = CreateEpubTargets(preparedBooks, requests, resultsBySequence);
        EpubAssessmentReuseResult epubReuse = AssessmentReusePolicy.PartitionEpub(
            previousSnapshot, epubTargets);
        IReadOnlyList<EpubAssessment> epubAssessments = [];
        if (assessEpubFormats is not null)
        {
            try
            {
                progress?.Report(new(
                    LibraryScanPhase.AssessingEpubFormats,
                    epubReuse.Reused.Count,
                    epubTargets.Count,
                    $"Assessing EPUB files: {epubReuse.Reused.Count:N0} reused; {epubReuse.FreshTargets.Count:N0} require inspection"));
                IProgress<EpubAssessmentProgress>? epubProgress = progress is null ? null : new EpubProgressAdapter(
                    progress, epubReuse.Reused.Count, epubTargets.Count);
                IReadOnlyList<EpubAssessment> fresh = epubReuse.FreshTargets.Count == 0
                    ? []
                    : await assessEpubFormats.ExecuteAsync(
                        epubReuse.FreshTargets,
                        options.MaxEpubAssessmentConcurrency,
                        EpubInspectionLimits.V1,
                        epubProgress,
                        cancellationToken).ConfigureAwait(false);
                epubAssessments = epubReuse.Reused.Concat(fresh)
                    .OrderBy(value => value.CalibreBookId.Value)
                    .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (EpubTargetAssessmentException exception)
            {
                return LibraryScanOutcome.Failure(new(
                    LibraryErrorCode.EpubAssessmentFailed,
                    $"EPUB assessment stopped at '{exception.RelativePath}' because of an unexpected {exception.FailureType}.",
                    "This does not show that another tool changed the library. Retry the scan; if the same EPUB fails again, report the file and technical reason."));
            }
            catch (Exception exception)
            {
                return LibraryScanOutcome.Failure(new(
                    LibraryErrorCode.EpubAssessmentFailed,
                    $"EPUB assessment stopped because of an unexpected {exception.GetType().Name}.",
                    "This does not show that another tool changed the library. Retry the scan; if it fails again, report the technical reason."));
            }
        }
        long epubCompleted = Stopwatch.GetTimestamp();

        IReadOnlyList<PdfAssessment> pdfAssessments = [];
        int reusedPdfCount = 0;
        int pdfTargetCount = 0;
        if (includePdfAssessments && assessPdfFormats is not null)
        {
            try
            {
                List<PdfAssessmentTarget> pdfTargets = CreatePdfTargets(preparedBooks, requests, resultsBySequence);
                pdfTargetCount = pdfTargets.Count;
                PdfAssessmentReuseResult pdfReuse = AssessmentReusePolicy.PartitionPdf(
                    previousSnapshot, pdfTargets);
                reusedPdfCount = pdfReuse.Reused.Count;
                progress?.Report(new(
                    LibraryScanPhase.AssessingPdfFormats,
                    pdfReuse.Reused.Count,
                    pdfTargets.Count,
                    $"Assessing PDF files: {pdfReuse.Reused.Count:N0} reused; {pdfReuse.FreshTargets.Count:N0} require inspection"));
                IProgress<PdfAssessmentProgress>? pdfProgress = progress is null ? null : new PdfProgressAdapter(
                    progress, pdfReuse.Reused.Count, pdfTargets.Count);
                IReadOnlyList<PdfAssessment> fresh = pdfReuse.FreshTargets.Count == 0
                    ? []
                    : await assessPdfFormats.ExecuteAsync(
                        pdfReuse.FreshTargets,
                        options.MaxPdfAssessmentConcurrency,
                        PdfInspectionLimits.V1,
                        pdfProgress,
                        cancellationToken).ConfigureAwait(false);
                pdfAssessments = pdfReuse.Reused.Concat(fresh)
                    .OrderBy(value => value.CalibreBookId.Value)
                    .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return LibraryScanOutcome.Failure(new(
                    LibraryErrorCode.PdfAssessmentFailed,
                    "The PDF files could not be assessed reliably.",
                    "Close tools changing the library and retry the scan."));
            }
        }
        long pdfCompleted = Stopwatch.GetTimestamp();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(LibraryScanPhase.GroupingExactDuplicates, 0, 1, "Grouping exact file duplicates"));
        IReadOnlyList<ExactBinaryDuplicateGroup> exactBinaryGroups =
            ExactBinaryDuplicateDetector.Detect(books, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(LibraryScanPhase.GroupingExactDuplicates, 1, 1, "Exact file duplicates grouped"));
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(
            LibraryScanPhase.GroupingExactMetadataDuplicates,
            0,
            1,
            "Grouping exact metadata duplicates"));
        HashSet<CalibreBookId> booksWithIncompleteAuthorReferences = catalog.Issues
            .Where(issue => issue.Code == "AUTHOR_REFERENCE_MISSING")
            .Select(issue => new CalibreBookId(issue.BookId))
            .ToHashSet();
        IReadOnlyList<ExactMetadataDuplicateGroup> exactMetadataGroups =
            ExactMetadataDuplicateDetector.Detect(
                books.Where(book => !booksWithIncompleteAuthorReferences.Contains(book.Id)),
                cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(
            LibraryScanPhase.GroupingExactMetadataDuplicates,
            1,
            1,
            "Exact metadata duplicates grouped"));

        IReadOnlyList<WorkLanguageCandidateGroup> workLanguageGroups = [];
        BookMatchingRunSummary matchingSummary = BookMatchingRunSummary.Unavailable(books.Count);
        if (discoverWorkLanguageCandidates is not null)
        {
            try
            {
                IProgress<WorkLanguageDiscoveryProgress>? matchingProgress = progress is null
                    ? null
                    : new MatchingProgressAdapter(progress);
                WorkLanguageDiscoveryResult matching = await discoverWorkLanguageCandidates.ExecuteAsync(
                    books,
                    epubAssessments,
                    epubTargets,
                    options.MaxContentSignatureConcurrency,
                    matchingProgress,
                    cancellationToken).ConfigureAwait(false);
                workLanguageGroups = matching.Groups;
                matchingSummary = matching.Summary;
                if (matching.LimitExceeded)
                {
                    findings.Add(new(
                        "MATCHING.CANDIDATE_LIMIT_EXCEEDED",
                        FindingSeverity.Warning,
                        "Expanded duplicate discovery stopped at its configured candidate limit.",
                        "Review exact results and retry after improving unusually broad metadata."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                workLanguageGroups = [];
                matchingSummary = BookMatchingRunSummary.Unavailable(books.Count);
                findings.Add(new(
                    "MATCHING.DISCOVERY_UNAVAILABLE",
                    FindingSeverity.Warning,
                    "Expanded duplicate discovery could not complete reliably.",
                    "Exact analysis is still available. Retry the scan to refresh expanded candidates."));
            }
        }
        long matchingCompleted = Stopwatch.GetTimestamp();

        Dictionary<FindingKey, LibraryFinding> uniqueFindings = [];
        foreach (LibraryFinding finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uniqueFindings.TryAdd(new(finding.BookId, finding.Format, finding.RelativePath, finding.Code), finding);
        }

        findings = uniqueFindings.Values
            .OrderBy(finding => finding.BookId?.Value ?? 0)
            .ThenBy(finding => finding.Format, StringComparer.Ordinal)
            .ThenBy(finding => finding.RelativePath, StringComparer.Ordinal)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        LibraryIdentity identity = new(catalog.LibraryUuid, catalog.SchemaVersion, validation.Location!.LibraryRoot);
        DateTimeOffset scannedAt = clock.GetUtcNow();
        LibrarySnapshot analysisSnapshot = new(
            identity,
            scannedAt,
            books,
            findings,
            exactBinaryGroups,
            exactMetadataGroups,
            epubAssessments,
            pdfAssessments: pdfAssessments,
            workLanguageCandidateGroups: workLanguageGroups,
            matchingRunSummary: matchingSummary);
        IReadOnlyList<ConsolidationRecommendation> recommendations;
        try
        {
            GenerateConsolidationRecommendationsUseCase generator = generateRecommendations ?? new(new());
            IProgress<RecommendationGenerationProgress>? recommendationProgress = progress is null
                ? null
                : new RecommendationProgressAdapter(progress);
            recommendations = await generator.ExecuteAsync(
                analysisSnapshot,
                recommendationProgress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LibraryScanOutcome.Failure(new(
                LibraryErrorCode.RecommendationGenerationFailed,
                "Consolidation recommendations could not be generated reliably.",
                "Retry the scan. No library content was changed."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        LibrarySnapshot snapshot = new(
            identity,
            scannedAt,
            books,
            findings,
            exactBinaryGroups,
            exactMetadataGroups,
            epubAssessments,
            recommendations,
            pdfAssessments,
            workLanguageGroups,
            matchingSummary);
        cancellationToken.ThrowIfCancellationRequested();
        long completed = Stopwatch.GetTimestamp();
        LogScanCompleted(
            _logger,
            catalog.Books.Count,
            requests.Count,
            epubTargets.Count,
            epubReuse.Reused.Count,
            pdfTargetCount,
            reusedPdfCount,
            ElapsedMilliseconds(scanStarted, catalogReadCompleted),
            ElapsedMilliseconds(catalogReadCompleted, resolutionCompleted),
            ElapsedMilliseconds(resolutionCompleted, hashingCompleted),
            ElapsedMilliseconds(hashingCompleted, epubCompleted),
            ElapsedMilliseconds(epubCompleted, pdfCompleted),
            ElapsedMilliseconds(pdfCompleted, matchingCompleted),
            ElapsedMilliseconds(matchingCompleted, completed),
            ElapsedMilliseconds(scanStarted, completed));
        progress?.Report(new(LibraryScanPhase.Completed, 1, 1, "Scan complete"));
        return LibraryScanOutcome.Success(snapshot);
    }

    private static CalibreBook MapBook(
        PreparedBook prepared,
        Dictionary<int, FormatHashResult> results,
        List<LibraryFinding> findings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CalibreBookId bookId = new(prepared.Record.Id);
        List<BookFormat> formats = [];
        foreach (PreparedFormat format in prepared.Formats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (format.Sequence is null)
            {
                formats.Add(new(format.Format, format.StoredName, format.RelativePath, FormatFileStatus.InvalidPath));
                continue;
            }

            FormatHashResult result = results[format.Sequence.Value];
            (FormatFileStatus status, string? findingCode, string? message, string? action) = result.Status switch
            {
                FormatHashResultStatus.Success => (FormatFileStatus.Present, null, null, null),
                FormatHashResultStatus.Missing => (
                    FormatFileStatus.Missing,
                    "FORMAT_FILE_MISSING",
                    $"The expected {format.Format} file is missing.",
                    "Use Calibre's Library maintenance tools or restore the file; this scan made no changes."),
                FormatHashResultStatus.Inaccessible => (
                    FormatFileStatus.Inaccessible,
                    "FORMAT_FILE_INACCESSIBLE",
                    $"The expected {format.Format} file could not be read safely.",
                    "Check file permissions, close tools locking the file, and retry."),
                FormatHashResultStatus.ChangedDuringHashing => (
                    FormatFileStatus.ChangedDuringHashing,
                    "FORMAT_FILE_CHANGED_DURING_HASHING",
                    $"The expected {format.Format} file changed while it was being hashed.",
                    "Wait for Calibre or file synchronization to finish, then retry."),
                _ => throw new InvalidOperationException("Unknown format hash result status."),
            };
            formats.Add(new(
                format.Format,
                format.StoredName,
                format.RelativePath,
                status,
                result.Fingerprint,
                result.Observation));
            if (findingCode is not null)
            {
                findings.Add(new(
                    findingCode,
                    FindingSeverity.Warning,
                    message!,
                    action!,
                    bookId,
                    format.Format,
                    format.RelativePath,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ReasonCode"] = result.ReasonCode ?? "Unknown",
                    }));
            }
        }

        return new(
            bookId,
            prepared.Record.Title,
            prepared.Record.AuthorSort,
            prepared.Record.Authors.Select(author => new BookAuthor(
                new CalibreAuthorId(author.Id),
                author.Name,
                author.SortName)),
            prepared.Record.Identifiers
                .OrderBy(identifier => identifier.Type, StringComparer.Ordinal)
                .ThenBy(identifier => identifier.Value, StringComparer.Ordinal)
                .Select(identifier => new BookIdentifier(identifier.Type, identifier.Value)),
            formats,
            prepared.Record.RelativeDirectory,
            prepared.Record.Publication is null
                ? BookPublicationMetadata.Empty
                : new BookPublicationMetadata(
                    prepared.Record.Publication.Publisher,
                    prepared.Record.Publication.PublicationDate,
                    prepared.Record.Publication.Series,
                    prepared.Record.Publication.SeriesIndex,
                    prepared.Record.Publication.Languages,
                    prepared.Record.Publication.HasCover));
    }

    private static LibraryFinding MapIssue(CalibreCatalogIssueRecord issue) => new(
        issue.Code,
        FindingSeverity.Warning,
        issue.Message,
        issue.SuggestedAction,
        new CalibreBookId(issue.BookId),
        issue.Format,
        issue.RelativePath);

    private static LibraryFinding CreateInvalidPathFinding(
        CalibreBookId bookId,
        string format,
        string relativeDirectory,
        string reason) => new(
        "MANAGED_PATH_INVALID",
        FindingSeverity.Warning,
        $"The expected {format} path is unsafe or invalid: {reason}",
        "Use Calibre's Library maintenance tools to inspect or repair this book.",
        bookId,
        format,
        relativeDirectory);

    private static void ReportProgress(
        IProgress<LibraryScanProgress>? progress,
        LibraryScanPhase phase,
        long completed,
        long total,
        string message)
    {
        if (completed == total || completed % 100 == 0)
        {
            progress?.Report(new(phase, completed, total, message));
        }
    }

    private static bool AreHashResultsValid(
        List<FormatHashRequest> requests,
        IReadOnlyList<FormatHashResult>? results)
    {
        if (results is null || results.Count != requests.Count)
        {
            return false;
        }

        int[] sequences = results.Select(result => result.Sequence).Order().ToArray();
        if (!sequences.SequenceEqual(Enumerable.Range(0, requests.Count)))
        {
            return false;
        }

        return results.All(result => result.Status switch
        {
            FormatHashResultStatus.Success => result.Fingerprint is not null
                && result.Observation is not null
                && result.Fingerprint.SizeInBytes == result.Observation.Length
                && result.ReasonCode is null,
            FormatHashResultStatus.Missing or FormatHashResultStatus.Inaccessible or FormatHashResultStatus.ChangedDuringHashing =>
                result.Fingerprint is null
                && result.Observation is null
                && !string.IsNullOrWhiteSpace(result.ReasonCode),
            _ => false,
        });
    }

    private sealed record PreparedBook(CalibreBookRecord Record, IReadOnlyList<PreparedFormat> Formats);

    private sealed record PreparedFormat(string Format, string StoredName, string RelativePath, int? Sequence);

    private static List<EpubAssessmentTarget> CreateEpubTargets(
        IReadOnlyList<PreparedBook> preparedBooks,
        IReadOnlyList<FormatHashRequest> requests,
        Dictionary<int, FormatHashResult> results)
    {
        Dictionary<int, FormatHashRequest> requestsBySequence = requests.ToDictionary(request => request.Sequence);
        List<EpubAssessmentTarget> targets = [];
        foreach (PreparedBook book in preparedBooks)
        {
            foreach (PreparedFormat format in book.Formats.Where(format => string.Equals(format.Format, "EPUB", StringComparison.OrdinalIgnoreCase)))
            {
                CalibreBookId bookId = new(book.Record.Id);
                if (format.Sequence is null)
                {
                    continue;
                }

                FormatHashResult result = results[format.Sequence.Value];
                ResolvedFormatPath path = requestsBySequence[format.Sequence.Value].Path;
                targets.Add(new(
                    bookId,
                    "EPUB",
                    format.RelativePath,
                    path.LibraryRoot,
                    path.FullPath,
                    MapFileStatus(result.Status),
                    result.Fingerprint,
                    result.Observation));
            }
        }

        return targets;
    }

    private static List<PdfAssessmentTarget> CreatePdfTargets(
        IReadOnlyList<PreparedBook> preparedBooks,
        IReadOnlyList<FormatHashRequest> requests,
        Dictionary<int, FormatHashResult> results)
    {
        Dictionary<int, FormatHashRequest> requestsBySequence = requests.ToDictionary(request => request.Sequence);
        List<PdfAssessmentTarget> targets = [];
        foreach (PreparedBook book in preparedBooks)
        {
            foreach (PreparedFormat format in book.Formats.Where(format => string.Equals(format.Format, "PDF", StringComparison.OrdinalIgnoreCase)))
            {
                CalibreBookId bookId = new(book.Record.Id);
                if (format.Sequence is null)
                {
                    targets.Add(new(bookId, "PDF", string.Empty, null, null, FormatFileStatus.InvalidPath, null, null));
                    continue;
                }

                FormatHashResult result = results[format.Sequence.Value];
                ResolvedFormatPath path = requestsBySequence[format.Sequence.Value].Path;
                targets.Add(new(
                    bookId,
                    "PDF",
                    format.RelativePath,
                    path.LibraryRoot,
                    path.FullPath,
                    MapFileStatus(result.Status),
                    result.Fingerprint,
                    result.Observation));
            }
        }

        return targets;
    }

    private static FormatFileStatus MapFileStatus(FormatHashResultStatus status) => status switch
    {
        FormatHashResultStatus.Success => FormatFileStatus.Present,
        FormatHashResultStatus.Missing => FormatFileStatus.Missing,
        FormatHashResultStatus.Inaccessible => FormatFileStatus.Inaccessible,
        FormatHashResultStatus.ChangedDuringHashing => FormatFileStatus.ChangedDuringHashing,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private sealed record FindingKey(
        CalibreBookId? BookId,
        string? Format,
        string? RelativePath,
        string Code);

    private sealed class ProgressAdapter(IProgress<LibraryScanProgress> progress) : IProgress<FormatHashProgress>
    {
        public void Report(FormatHashProgress value)
        {
            bool useBytes = value.TotalBytes > 0;
            progress.Report(new(
                LibraryScanPhase.HashingFormats,
                useBytes ? Math.Min(value.CompletedBytes, value.TotalBytes) : value.CompletedFiles,
                useBytes ? value.TotalBytes : value.TotalFiles,
                value.Message));
        }
    }

    private sealed class EpubProgressAdapter(
        IProgress<LibraryScanProgress> progress,
        int reusedFiles,
        int totalFiles) : IProgress<EpubAssessmentProgress>
    {
        public void Report(EpubAssessmentProgress value) => progress.Report(new(
            LibraryScanPhase.AssessingEpubFormats,
            reusedFiles + value.CompletedFiles,
            totalFiles,
            value.ActiveFiles == 0
                ? $"Assessing EPUB files: {reusedFiles + value.CompletedFiles:N0} of {totalFiles:N0} complete ({reusedFiles:N0} reused)"
                : $"Assessing EPUB files: {reusedFiles + value.CompletedFiles:N0} of {totalFiles:N0} complete; {value.ActiveFiles:N0} active ({value.ActiveStageSummary}); {reusedFiles:N0} reused"));
    }

    private sealed class PdfProgressAdapter(
        IProgress<LibraryScanProgress> progress,
        int reusedFiles,
        int totalFiles) : IProgress<PdfAssessmentProgress>
    {
        public void Report(PdfAssessmentProgress value) => progress.Report(new(
            LibraryScanPhase.AssessingPdfFormats,
            reusedFiles + value.CompletedFiles,
            totalFiles,
            string.IsNullOrWhiteSpace(value.CurrentRelativePath)
                ? $"Assessing PDF files: {reusedFiles + value.CompletedFiles:N0} of {totalFiles:N0} complete ({reusedFiles:N0} reused)"
                : $"Assessing PDF files: {reusedFiles + value.CompletedFiles:N0} of {totalFiles:N0} complete — {value.Stage}: {value.CurrentRelativePath}; {reusedFiles:N0} reused"));
    }

    private sealed class MatchingProgressAdapter(IProgress<LibraryScanProgress> progress) :
        IProgress<WorkLanguageDiscoveryProgress>
    {
        public void Report(WorkLanguageDiscoveryProgress value)
        {
            (LibraryScanPhase phase, string message) = value.Phase switch
            {
                WorkLanguageDiscoveryPhase.BuildingProfiles =>
                    (LibraryScanPhase.BuildingMatchingProfiles, "Building canonical author identities"),
                WorkLanguageDiscoveryPhase.GeneratingCandidates =>
                    (LibraryScanPhase.GeneratingMatchingCandidates, "Searching for duplicate works within author groups"),
                WorkLanguageDiscoveryPhase.InspectingContent =>
                    (LibraryScanPhase.InspectingCandidateContent, "Confirming candidate books with EPUB content evidence"),
                WorkLanguageDiscoveryPhase.Clustering =>
                    (LibraryScanPhase.GroupingWorkLanguageCandidates, "Publishing confirmed work-language groups"),
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            };
            progress.Report(new(
                phase,
                value.Completed,
                value.Total,
                string.IsNullOrWhiteSpace(value.Detail) ? message : $"{message}: {value.Detail}"));
        }
    }

    private sealed class RecommendationProgressAdapter(IProgress<LibraryScanProgress> progress) : IProgress<RecommendationGenerationProgress>
    {
        public void Report(RecommendationGenerationProgress value) => progress.Report(new(
            LibraryScanPhase.GeneratingConsolidationRecommendations,
            value.CompletedGroups,
            value.TotalGroups,
            $"Generating consolidation recommendations: {value.CompletedGroups} of {value.TotalGroups} complete"));
    }

    private static long ElapsedMilliseconds(long started, long completed) =>
        (long)Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;

    [LoggerMessage(400, LogLevel.Information,
        "Library scan completed. Books={BookCount}, Formats={FormatCount}, EpubTargets={EpubTargets}, ReusedEpubAssessments={ReusedEpubAssessments}, PdfTargets={PdfTargets}, ReusedPdfAssessments={ReusedPdfAssessments}, CatalogMilliseconds={CatalogMilliseconds}, ResolutionMilliseconds={ResolutionMilliseconds}, HashingMilliseconds={HashingMilliseconds}, EpubMilliseconds={EpubMilliseconds}, PdfMilliseconds={PdfMilliseconds}, MatchingMilliseconds={MatchingMilliseconds}, RecommendationAndPublicationMilliseconds={RecommendationAndPublicationMilliseconds}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogScanCompleted(
        ILogger logger,
        int bookCount,
        int formatCount,
        int epubTargets,
        int reusedEpubAssessments,
        int pdfTargets,
        int reusedPdfAssessments,
        long catalogMilliseconds,
        long resolutionMilliseconds,
        long hashingMilliseconds,
        long epubMilliseconds,
        long pdfMilliseconds,
        long matchingMilliseconds,
        long recommendationAndPublicationMilliseconds,
        long totalMilliseconds);
}
