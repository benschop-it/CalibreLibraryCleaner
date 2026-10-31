using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record FrontMatterAuthorProgress(
    int CompletedFormats,
    int TotalFormats,
    int CacheHits,
    int Inspections,
    string Stage);

public sealed class InspectFrontMatterAuthorsUseCase(
    ILibraryPathResolver pathResolver,
    IEpubFrontMatterAuthorInspector epubInspector,
    IPdfFrontMatterAuthorInspector pdfInspector,
    IFrontMatterAuthorCache cache)
{
    public async Task<FinalAuthorReviewWorkspace> ExecuteAsync(
        LibraryState state,
        FinalAuthorReviewWorkspace initial,
        IProgress<FrontMatterAuthorProgress>? progress,
        CancellationToken cancellationToken,
        bool inspectAllRecords = false,
        IReadOnlyCollection<CalibreBookId>? targetBookIds = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.GenerationId != state.GenerationId
            || initial.Revision != state.Revision
            || !PathsEqual(initial.LibraryRoot, state.Snapshot.Identity.LibraryRoot))
            throw new InvalidOperationException("The final author review workspace is stale.");
        HashSet<CalibreBookId>? targets = targetBookIds?.ToHashSet();
        if (targets is not null && (targets.Count == 0 || targets.Count != targetBookIds!.Count
                || !targets.IsSubsetOf(state.Snapshot.Books.Select(value => value.Id))
                || inspectAllRecords))
            throw new ArgumentException("Selected opening-page inspection requires distinct current record IDs.", nameof(targetBookIds));
        LibraryValidationOutcome validation = await pathResolver.ValidateAsync(
            initial.LibraryRoot, cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess)
            throw new InvalidOperationException("The final author review library is unavailable.");
        Dictionary<CalibreBookId, CalibreBook> books = state.Snapshot.Books.ToDictionary(value => value.Id);
        HashSet<CalibreBookId> selected = initial.Subjects
            .Where(value => value.NeedsInspection).Select(value => value.BookId).ToHashSet();
        (CalibreBook Book, BookFormat Format)[] demands = books.Values
            .Where(book => targets is not null ? targets.Contains(book.Id) : inspectAllRecords || selected.Contains(book.Id))
            .SelectMany(book => book.Formats
                .Where(format => format.Format is "EPUB" or "PDF"
                    && format.FileStatus == FormatFileStatus.Present
                    && format.Fingerprint is not null
                    && format.Observation is not null)
                .Select(format => (Book: book, Format: format)))
            .OrderBy(value => value.Book.Id.Value)
            .ThenBy(value => value.Format.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        List<FrontMatterAuthorObservation> observations = [];
        Dictionary<FrontMatterAuthorCacheKey, FrontMatterAuthorObservation> completed = initial.FrontMatterObservations
            .Where(value => targets is null || value.Status != FrontMatterAuthorStatus.Unavailable)
            .DistinctBy(value => new FrontMatterAuthorCacheKey(
                value.Format, value.Fingerprint, value.ExtractorVersion, value.LimitsVersion))
            .ToDictionary(value => new FrontMatterAuthorCacheKey(
                value.Format, value.Fingerprint, value.ExtractorVersion, value.LimitsVersion));
        int cacheHits = 0;
        int inspections = 0;
        for (int index = 0; index < demands.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (CalibreBook book, BookFormat format) = demands[index];
            bool epub = format.Format == "EPUB";
            FrontMatterAuthorCacheKey key = new(
                format.Format,
                format.Fingerprint!,
                epub ? FrontMatterAuthorVersions.EpubExtractor : FrontMatterAuthorVersions.PdfExtractor,
                FrontMatterAuthorVersions.Limits);
            FrontMatterAuthorObservation? observation = completed.GetValueOrDefault(key)
                ?? await TryReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (observation is not null && (observation.Format != key.Format || observation.Fingerprint != key.Fingerprint
                    || observation.ExtractorVersion != key.ExtractorVersion || observation.LimitsVersion != key.LimitsVersion)
                || observation?.Status == FrontMatterAuthorStatus.Unavailable && !completed.ContainsKey(key))
                observation = null;
            if (observation is not null)
            {
                cacheHits++;
            }
            else
            {
                ResolvedFormatPathOutcome resolved = pathResolver.ResolveFormat(
                    validation.Location!, book.RelativeDirectory, format.StoredFileName, format.Format);
                if (!resolved.IsSuccess
                    || !string.Equals(
                        resolved.Path!.RelativePath.Replace('\\', '/'),
                        format.ExpectedRelativePath.Replace('\\', '/'), StringComparison.Ordinal))
                {
                    observation = Unavailable(key, $"{format.Format}_FRONT_MATTER.UNSAFE_PATH");
                }
                else
                {
                    inspections++;
                    observation = epub
                        ? await epubInspector.InspectFrontMatterAuthorsAsync(
                            new(
                                book.Id, resolved.Path.LibraryRoot, resolved.Path.FullPath,
                                resolved.Path.RelativePath, format.Fingerprint!, format.Observation!,
                                EpubInspectionLimits.V1),
                            FrontMatterInspectionLimits.V1,
                            cancellationToken).ConfigureAwait(false)
                        : await pdfInspector.InspectFrontMatterAuthorsAsync(
                            new(
                                book.Id, resolved.Path.LibraryRoot, resolved.Path.FullPath,
                                resolved.Path.RelativePath, format.Fingerprint!, format.Observation!,
                                PdfInspectionLimits.V1),
                            FrontMatterInspectionLimits.V1,
                            cancellationToken).ConfigureAwait(false);
                }
                await TryWriteAsync(observation, cancellationToken).ConfigureAwait(false);
            }
            observations.Add(observation);
            completed[key] = observation;
            progress?.Report(new(index + 1, demands.Length, cacheHits, inspections,
                $"{format.Format} front matter"));
        }
        return PrepareFinalAuthorReviewUseCase.Execute(
            state,
            initial.EmbeddedObservations,
            initial.MetadataCacheHitCount,
            initial.MetadataInspectionCount,
            observations.Concat(initial.FrontMatterObservations)
                .DistinctBy(value => new
                {
                    value.Format,
                    value.Fingerprint,
                    value.ExtractorVersion,
                    value.LimitsVersion,
                }).ToArray(),
            cacheHits,
            inspections);

        async Task<FrontMatterAuthorObservation?> TryReadAsync(
            FrontMatterAuthorCacheKey keyValue,
            CancellationToken token)
        {
            try { return await cache.TryReadAsync(keyValue, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsCacheFailure(exception)) { return null; }
        }

        async Task TryWriteAsync(FrontMatterAuthorObservation value, CancellationToken token)
        {
            try { await cache.WriteAsync(value, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsCacheFailure(exception)) { }
        }
    }

    private static FrontMatterAuthorObservation Unavailable(
        FrontMatterAuthorCacheKey key,
        string problemCode) => new(
        key.Format,
        key.Fingerprint,
        key.ExtractorVersion,
        key.LimitsVersion,
        FrontMatterAuthorStatus.Unavailable,
        [],
        0,
        0,
        0,
        false,
        problemCode);

    private static bool IsCacheFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or InvalidOperationException
        or ArgumentException
        or OverflowException;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
