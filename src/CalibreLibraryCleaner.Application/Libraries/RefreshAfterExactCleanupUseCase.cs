using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Libraries;

public enum PostExactRefreshPhase
{
    LoadingState,
    ReadingCatalog,
    Reconciling,
    ResolvingFiles,
    HashingTransferTargets,
    Publishing,
    Completed,
}

public sealed record PostExactRefreshProgress(
    PostExactRefreshPhase Phase,
    int Completed,
    int Total,
    string Message);

public sealed record PostExactRefreshMetrics(
    int CatalogRecordCount,
    int CatalogFormatCount,
    int ReusedFingerprintCount,
    int ReboundTransferCount,
    int TargetedHashCount,
    int ReusedAssessmentCount,
    int FreshAssessmentCount,
    int UnexplainedDifferenceCount,
    long TotalMilliseconds);

public sealed record PostExactRefreshResult(
    LibraryState? State,
    PostExactRefreshMetrics Metrics,
    string? ErrorCode = null,
    string? Explanation = null)
{
    public bool IsSuccess => State is not null && ErrorCode is null;

    public static PostExactRefreshResult Failure(
        string code,
        string explanation,
        PostExactRefreshMetrics metrics) => new(null, metrics, code, explanation);
}

public sealed partial class RefreshAfterExactCleanupUseCase(
    ILibraryStateSession stateSession,
    ILibraryStateStore stateStore,
    ILibraryPathResolver pathResolver,
    ICalibreMetadataReader metadataReader,
    IFormatFileHasher formatFileHasher,
    IFormatFileProbe formatFileProbe,
    IResidualAnalysisFactsPreparer residualFactsPreparer,
    IClock clock,
    LibraryAnalysisOptions options,
    ILogger<RefreshAfterExactCleanupUseCase>? logger = null)
{
    private readonly ILogger<RefreshAfterExactCleanupUseCase> _logger = logger
        ?? NullLogger<RefreshAfterExactCleanupUseCase>.Instance;

    public async Task<PostExactRefreshResult> ExecuteAsync(
        string libraryRoot,
        IProgress<PostExactRefreshProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        long started = Stopwatch.GetTimestamp();
        int catalogRecordCount = 0;
        int catalogFormatCount = 0;
        int reusedCount = 0;
        int reboundCount = 0;
        int targetedHashCount = 0;
        int reusedAssessmentCount = 0;
        int freshAssessmentCount = 0;
        int unexplainedCount = 0;

        PostExactRefreshResult Failure(string code, string explanation) =>
            PostExactRefreshResult.Failure(code, explanation, Metrics());

        PostExactRefreshMetrics Metrics() => new(
            catalogRecordCount,
            catalogFormatCount,
            reusedCount,
            reboundCount,
            targetedHashCount,
            reusedAssessmentCount,
            freshAssessmentCount,
            unexplainedCount,
            ElapsedMilliseconds(started, Stopwatch.GetTimestamp()));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(PostExactRefreshPhase.LoadingState, 0, 1, "Loading post-Exact state"));
        LibraryState? postExactState = stateSession.GetCurrent(libraryRoot);
        if (postExactState is null)
        {
            LibraryStateSessionOutcome loaded = await stateSession.LoadAsync(
                libraryRoot, cancellationToken).ConfigureAwait(false);
            if (loaded.IsSuccess) postExactState = loaded.State;
        }
        if (postExactState is not
            {
                IsAuthoritative: true,
                IsWorkflowCheckpointCurrent: true,
                WorkflowCheckpoint:
                {
                    Phase: LibraryWorkflowPhase.CandidatePreparationReady,
                    Source: null,
                },
            })
            return Failure(
                "POST_EXACT.STATE_NOT_READY",
                "Exact cleanup must complete successfully before candidate preparation.");

        PostExactRefreshBasis? basis;
        try
        {
            basis = await stateStore.ReadPostExactRefreshBasisAsync(
                libraryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            return Failure("POST_EXACT.BASIS_UNAVAILABLE", exception.Message);
        }
        if (basis is null)
            return Failure("POST_EXACT.BASIS_UNAVAILABLE", "No completed Exact refresh basis exists.");

        LibraryValidationOutcome validation = await pathResolver.ValidateAsync(
            libraryRoot, cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess
            || !PathsEqual(validation.Location!.LibraryRoot, postExactState.Snapshot.Identity.LibraryRoot))
            return Failure(
                "POST_EXACT.LIBRARY_INVALID",
                validation.Error?.Message ?? "The selected library does not match authoritative state.");

        progress?.Report(new(PostExactRefreshPhase.ReadingCatalog, 0, 1, "Reading current catalog"));
        CalibreCatalogReadOutcome read = await metadataReader.ReadAsync(
            validation.Location, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Failure("POST_EXACT.CATALOG_READ_FAILED", read.Error!.Message);
        CalibreCatalogRecord catalog = read.Catalog!;
        catalogRecordCount = catalog.Books.Count;
        catalogFormatCount = catalog.Books.Sum(value => value.Formats.Count);

        progress?.Report(new(PostExactRefreshPhase.Reconciling, 0, 1, "Reconciling Exact cleanup changes"));
        PostExactReconciliationResult reconciliation = PostExactCleanupReconciliationPolicy.Reconcile(
            basis.PreExactState,
            postExactState,
            basis.CompletedExactDeltas,
            catalog);
        unexplainedCount = reconciliation.Issues.Count;
        if (!reconciliation.IsSuccess)
        {
            LogRefreshRejected(_logger, catalogRecordCount, catalogFormatCount, unexplainedCount);
            return Failure(
                "POST_EXACT.UNEXPLAINED_CHANGE",
                "The current catalog contains changes not explained by completed Exact cleanup. Run a new exact analysis.");
        }

        Dictionary<PostExactAssociationKey, PostExactAssociationDecision> currentDecisions = reconciliation.Decisions
            .Where(value => value.Disposition is PostExactAssociationDisposition.Unchanged
                or PostExactAssociationDisposition.Transferred
                or PostExactAssociationDisposition.TargetedHashRequired)
            .ToDictionary(value => value.Association);
        Dictionary<PostExactAssociationKey, BookFormat> preFormats = basis.PreExactState.Snapshot.Books
            .SelectMany(book => book.Formats.Select(format => new
            {
                Key = new PostExactAssociationKey(book.Id, format.Format),
                Format = format,
            }))
            .ToDictionary(value => value.Key, value => value.Format);
        List<ResolvedAssociation> resolvedAssociations = [];
        List<FormatHashRequest> hashRequests = [];
        int resolvedCount = 0;
        progress?.Report(new(
            PostExactRefreshPhase.ResolvingFiles,
            0,
            catalogFormatCount,
            "Resolving post-Exact format files"));
        foreach (CalibreBookRecord book in catalog.Books.OrderBy(value => value.Id))
        {
            foreach (CalibreFormatRecord format in book.Formats
                         .OrderBy(value => value.Format, StringComparer.Ordinal)
                         .ThenBy(value => value.StoredName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PostExactAssociationKey key = new(new(book.Id), format.Format);
                if (!currentDecisions.TryGetValue(key, out PostExactAssociationDecision? decision))
                    return Failure(
                        "POST_EXACT.ASSOCIATION_UNEXPLAINED",
                        $"Record {book.Id} has no explained {key.Format} association.");
                ResolvedFormatPathOutcome resolved = pathResolver.ResolveFormat(
                    validation.Location, book.RelativeDirectory, format.StoredName, key.Format);
                if (!resolved.IsSuccess)
                    return Failure(
                        "POST_EXACT.PATH_INVALID",
                        $"Record {book.Id} has an unsafe or invalid {key.Format} path.");
                BookFormat? preFormat = preFormats.GetValueOrDefault(key);
                if (decision.Disposition == PostExactAssociationDisposition.Unchanged
                    && (preFormat is null
                        || !PathsEqual(resolved.Path!.RelativePath, preFormat.ExpectedRelativePath)))
                    return Failure(
                        "POST_EXACT.PATH_CHANGED",
                        $"Record {book.Id} has an unexplained {key.Format} path change.");
                FormatFileObservation? probeObservation = null;
                if (decision.Disposition == PostExactAssociationDisposition.Unchanged)
                {
                    FormatFileProbeResult probe = await formatFileProbe.ProbeAsync(
                        resolved.Path!, cancellationToken).ConfigureAwait(false);
                    if (probe.Status != FormatFileProbeStatus.Success
                        || probe.Observation is null
                        || probe.Observation != preFormat!.Observation)
                        return Failure(
                            "POST_EXACT.UNCHANGED_FILE_INVALID",
                            $"Record {book.Id} has a missing, inaccessible, unsafe, or changed {key.Format} file.");
                    probeObservation = probe.Observation;
                }
                int? hashSequence = null;
                if (decision.Disposition == PostExactAssociationDisposition.TargetedHashRequired)
                {
                    hashSequence = hashRequests.Count;
                    hashRequests.Add(new(hashSequence.Value, key.BookId, key.Format, resolved.Path!));
                }
                resolvedAssociations.Add(new(
                    key, format.StoredName, resolved.Path!, decision, preFormat, probeObservation, hashSequence));
                resolvedCount++;
                progress?.Report(new(
                    PostExactRefreshPhase.ResolvingFiles,
                    resolvedCount,
                    catalogFormatCount,
                    "Resolving post-Exact format files"));
            }
        }

        IReadOnlyList<FormatHashResult> hashResults = [];
        if (hashRequests.Count > 0)
        {
            progress?.Report(new(
                PostExactRefreshPhase.HashingTransferTargets,
                0,
                hashRequests.Count,
                "Verifying transferred format bytes"));
            try
            {
                hashResults = await formatFileHasher.HashAsync(
                    hashRequests,
                    options.MaxHashConcurrency,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Failure(
                    "POST_EXACT.TARGET_HASH_FAILED",
                    "Transferred format targets could not be hashed reliably.");
            }
            if (!ValidHashResults(hashRequests, hashResults))
                return Failure(
                    "POST_EXACT.TARGET_HASH_FAILED",
                    "Transferred format target hashing returned incomplete results.");
            targetedHashCount = hashResults.Count;
        }
        Dictionary<int, FormatHashResult> hashes = hashResults.ToDictionary(value => value.Sequence);
        Dictionary<PostExactAssociationKey, BookFormat> physicalFormats = [];
        foreach (ResolvedAssociation association in resolvedAssociations)
        {
            if (association.Decision.ExpectedFingerprint is not { } expectedFingerprint)
                return Failure(
                    "POST_EXACT.FINGERPRINT_EVIDENCE_MISSING",
                    $"Record {association.Key.BookId.Value} has incomplete {association.Key.Format} identity evidence.");
            FormatFileFingerprint fingerprint;
            FormatFileObservation observation;
            if (association.HashSequence is int sequence)
            {
                FormatHashResult hash = hashes[sequence];
                if (hash.Fingerprint != expectedFingerprint)
                    return Failure(
                        "POST_EXACT.TRANSFER_FINGERPRINT_MISMATCH",
                        $"Transferred {association.Key.Format} bytes do not match completed Exact cleanup evidence.");
                fingerprint = hash.Fingerprint!;
                observation = hash.Observation!;
                reboundCount++;
            }
            else
            {
                fingerprint = expectedFingerprint;
                observation = association.ProbeObservation!;
                reusedCount++;
            }
            physicalFormats.Add(association.Key, new(
                association.Key.Format,
                association.StoredName,
                association.Path.RelativePath,
                FormatFileStatus.Present,
                fingerprint,
                observation));
        }

        DateTimeOffset refreshedAt = clock.GetUtcNow().ToUniversalTime();
        if (refreshedAt < postExactState.ProjectedAtUtc) refreshedAt = postExactState.ProjectedAtUtc;
        CalibreBook[] books = catalog.Books.OrderBy(value => value.Id)
            .Select(value => MapBook(value, physicalFormats))
            .ToArray();
        Dictionary<PostExactAssociationKey, ResolvedAssociation> resolvedByKey = resolvedAssociations
            .ToDictionary(value => value.Key);
        EpubAssessmentTarget[] epubTargets = physicalFormats
            .Where(value => value.Key.Format == "EPUB")
            .OrderBy(value => value.Key.BookId.Value)
            .Select(value => ToEpubTarget(value.Key, value.Value, resolvedByKey[value.Key].Path))
            .ToArray();
        PdfAssessmentTarget[] pdfTargets = physicalFormats
            .Where(value => value.Key.Format == "PDF")
            .OrderBy(value => value.Key.BookId.Value)
            .Select(value => ToPdfTarget(value.Key, value.Value, resolvedByKey[value.Key].Path))
            .ToArray();
        ResidualAnalysisFacts facts = await residualFactsPreparer.PrepareAsync(
            validation.Location.LibraryRoot,
            epubTargets,
            pdfTargets,
            cancellationToken).ConfigureAwait(false);
        reusedAssessmentCount = facts.ReusedEpubAssessmentCount + facts.ReusedPdfAssessmentCount;
        freshAssessmentCount = facts.FreshEpubAssessmentCount + facts.FreshPdfAssessmentCount;
        LibrarySnapshot snapshot = new(
            postExactState.Snapshot.Identity,
            refreshedAt,
            books,
            catalog.Issues.Select(MapIssue),
            ExactBinaryDuplicateDetector.Detect(books, cancellationToken),
            epubAssessments: facts.EpubAssessments,
            pdfAssessments: facts.PdfAssessments);
        progress?.Report(new(PostExactRefreshPhase.Publishing, 0, 1, "Publishing refreshed candidate state"));
        LibraryStateSessionOutcome published = await stateSession.StartFromPostExactRefreshAsync(
            snapshot,
            new(postExactState.GenerationId, postExactState.Revision),
            cancellationToken).ConfigureAwait(false);
        if (!published.IsSuccess)
            return Failure(
                "POST_EXACT.PUBLICATION_FAILED",
                published.Explanation ?? "The refreshed candidate generation could not be published.");
        progress?.Report(new(PostExactRefreshPhase.Completed, 1, 1, "Post-Exact refresh complete"));
        PostExactRefreshMetrics metrics = Metrics();
        LogRefreshCompleted(
            _logger,
            metrics.CatalogRecordCount,
            metrics.CatalogFormatCount,
            metrics.ReusedFingerprintCount,
            metrics.ReboundTransferCount,
            metrics.TargetedHashCount,
            metrics.ReusedAssessmentCount,
            metrics.FreshAssessmentCount,
            metrics.TotalMilliseconds);
        return new(published.State, metrics);
    }

    private static CalibreBook MapBook(
        CalibreBookRecord record,
        Dictionary<PostExactAssociationKey, BookFormat> formats) => new(
        new(record.Id),
        record.Title,
        record.AuthorSort,
        record.Authors.Select(value => new BookAuthor(new(value.Id), value.Name, value.SortName)),
        record.Identifiers.Select(value => new BookIdentifier(value.Type, value.Value)),
        record.Formats.Select(value => formats[new(new(record.Id), value.Format)]),
        record.RelativeDirectory,
        record.Publication is null
            ? BookPublicationMetadata.Empty
            : new(
                record.Publication.Publisher,
                record.Publication.PublicationDate,
                record.Publication.Series,
                record.Publication.SeriesIndex,
                record.Publication.Languages,
                record.Publication.HasCover));

    private static LibraryFinding MapIssue(CalibreCatalogIssueRecord issue) => new(
        issue.Code,
        FindingSeverity.Warning,
        issue.Message,
        issue.SuggestedAction,
        new(issue.BookId),
        issue.Format,
        issue.RelativePath);

    private static EpubAssessmentTarget ToEpubTarget(
        PostExactAssociationKey key,
        BookFormat format,
        ResolvedFormatPath path) => new(
        key.BookId,
        key.Format,
        path.RelativePath,
        path.LibraryRoot,
        path.FullPath,
        format.FileStatus,
        format.Fingerprint,
        format.Observation);

    private static PdfAssessmentTarget ToPdfTarget(
        PostExactAssociationKey key,
        BookFormat format,
        ResolvedFormatPath path) => new(
        key.BookId,
        key.Format,
        path.RelativePath,
        path.LibraryRoot,
        path.FullPath,
        format.FileStatus,
        format.Fingerprint,
        format.Observation);

    private static bool ValidHashResults(
        List<FormatHashRequest> requests,
        IReadOnlyList<FormatHashResult>? results) => results is not null
        && results.Count == requests.Count
        && results.Select(value => value.Sequence).Order().SequenceEqual(Enumerable.Range(0, requests.Count))
        && results.All(value => value.Status == FormatHashResultStatus.Success
            && value.Fingerprint is not null
            && value.Observation is not null
            && value.Fingerprint.SizeInBytes == value.Observation.Length
            && value.ReasonCode is null);

    private static bool PathsEqual(string first, string second) => string.Equals(
        NormalizePath(first),
        NormalizePath(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizePath(string value) => Path.TrimEndingDirectorySeparator(
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

    private static long ElapsedMilliseconds(long started, long completed) =>
        (long)Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;

    [LoggerMessage(1, LogLevel.Warning,
        "Post-Exact refresh rejected catalog changes. CatalogRecords={CatalogRecords}, CatalogFormats={CatalogFormats}, UnexplainedDifferences={UnexplainedDifferences}.")]
    private static partial void LogRefreshRejected(
        ILogger logger,
        int catalogRecords,
        int catalogFormats,
        int unexplainedDifferences);

    [LoggerMessage(2, LogLevel.Information,
        "Post-Exact refresh completed. CatalogRecords={CatalogRecords}, CatalogFormats={CatalogFormats}, ReusedFingerprints={ReusedFingerprints}, ReboundTransfers={ReboundTransfers}, TargetedHashes={TargetedHashes}, ReusedAssessments={ReusedAssessments}, FreshAssessments={FreshAssessments}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogRefreshCompleted(
        ILogger logger,
        int catalogRecords,
        int catalogFormats,
        int reusedFingerprints,
        int reboundTransfers,
        int targetedHashes,
        int reusedAssessments,
        int freshAssessments,
        long totalMilliseconds);

    private sealed record ResolvedAssociation(
        PostExactAssociationKey Key,
        string StoredName,
        ResolvedFormatPath Path,
        PostExactAssociationDecision Decision,
        BookFormat? PreExactFormat,
        FormatFileObservation? ProbeObservation,
        int? HashSequence);
}
