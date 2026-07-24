using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed class ScanLibraryUseCase(
    ILibraryPathResolver pathResolver,
    ICalibreMetadataReader metadataReader,
    IFormatFileHasher formatFileHasher,
    IClock clock,
    LibraryAnalysisOptions options,
    AssessEpubFormatsUseCase? assessEpubFormats = null,
    GenerateConsolidationRecommendationsUseCase? generateRecommendations = null)
{
    public async Task<LibraryScanOutcome> ExecuteAsync(
        string? candidatePath,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(LibraryScanPhase.Validating, 0, 1, "Validating library"));
        LibraryValidationOutcome validation = await pathResolver
            .ValidateAsync(candidatePath, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return LibraryScanOutcome.Failure(validation.Error!);
        }

        progress?.Report(new(LibraryScanPhase.Validating, 1, 1, "Library validated"));
        CalibreCatalogReadOutcome readOutcome = await metadataReader
            .ReadAsync(validation.Location!, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!readOutcome.IsSuccess)
        {
            return LibraryScanOutcome.Failure(readOutcome.Error!);
        }

        CalibreCatalogRecord catalog = readOutcome.Catalog!;
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

        Dictionary<int, FormatHashResult> resultsBySequence = hashResults.ToDictionary(result => result.Sequence);
        List<CalibreBook> books = preparedBooks
            .Select(book => MapBook(book, resultsBySequence, findings, cancellationToken))
            .ToList();
        IReadOnlyList<FormatAssessment> epubAssessments = [];
        if (assessEpubFormats is not null)
        {
            try
            {
                List<EpubAssessmentTarget> epubTargets = CreateEpubTargets(preparedBooks, requests, resultsBySequence);
                IProgress<EpubAssessmentProgress>? epubProgress = progress is null ? null : new EpubProgressAdapter(progress);
                epubAssessments = await assessEpubFormats.ExecuteAsync(
                    epubTargets,
                    options.MaxEpubAssessmentConcurrency,
                    EpubInspectionLimits.V1,
                    epubProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return LibraryScanOutcome.Failure(new(
                    LibraryErrorCode.EpubAssessmentFailed,
                    "The EPUB files could not be assessed reliably.",
                    "Close tools changing the library and retry the scan."));
            }
        }

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
            epubAssessments);
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
            recommendations);
        cancellationToken.ThrowIfCancellationRequested();
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
                    targets.Add(new(bookId, "EPUB", string.Empty, null, null, FormatFileStatus.InvalidPath, null, null));
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

    private sealed class EpubProgressAdapter(IProgress<LibraryScanProgress> progress) : IProgress<EpubAssessmentProgress>
    {
        public void Report(EpubAssessmentProgress value) => progress.Report(new(
            LibraryScanPhase.AssessingEpubFormats,
            value.CompletedFiles,
            value.TotalFiles,
            string.IsNullOrWhiteSpace(value.CurrentRelativePath)
                ? $"Assessing EPUB files: {value.CompletedFiles} of {value.TotalFiles} complete"
                : $"Assessing EPUB files: {value.CompletedFiles} of {value.TotalFiles} complete — {value.Stage}: {value.CurrentRelativePath}"));
    }

    private sealed class RecommendationProgressAdapter(IProgress<LibraryScanProgress> progress) : IProgress<RecommendationGenerationProgress>
    {
        public void Report(RecommendationGenerationProgress value) => progress.Report(new(
            LibraryScanPhase.GeneratingConsolidationRecommendations,
            value.CompletedGroups,
            value.TotalGroups,
            $"Generating consolidation recommendations: {value.CompletedGroups} of {value.TotalGroups} complete"));
    }
}
