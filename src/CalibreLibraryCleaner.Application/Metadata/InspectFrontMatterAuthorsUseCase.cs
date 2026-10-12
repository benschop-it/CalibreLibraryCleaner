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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.GenerationId != state.GenerationId
            || initial.Revision != state.Revision
            || !PathsEqual(initial.LibraryRoot, state.Snapshot.Identity.LibraryRoot))
            throw new InvalidOperationException("The final author review workspace is stale.");
        LibraryValidationOutcome validation = await pathResolver.ValidateAsync(
            initial.LibraryRoot, cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess)
            throw new InvalidOperationException("The final author review library is unavailable.");
        Dictionary<CalibreBookId, CalibreBook> books = state.Snapshot.Books.ToDictionary(value => value.Id);
        (CalibreBook Book, BookFormat Format)[] demands = initial.Subjects
            .Where(value => value.NeedsInspection)
            .SelectMany(subject => books[subject.BookId].Formats
                .Where(format => format.Format is "EPUB" or "PDF"
                    && format.FileStatus == FormatFileStatus.Present
                    && format.Fingerprint is not null
                    && format.Observation is not null)
                .Select(format => (Book: books[subject.BookId], Format: format)))
            .OrderBy(value => value.Book.Id.Value)
            .ThenBy(value => value.Format.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        List<FrontMatterAuthorObservation> observations = [];
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
            FrontMatterAuthorObservation? observation = await TryReadAsync(key, cancellationToken)
                .ConfigureAwait(false);
            if (observation is not null)
            {
                cacheHits++;
            }
            else
            {
                ResolvedFormatPathOutcome resolved = pathResolver.ResolveFormat(
                    validation.Location!, book.RelativeDirectory, format.StoredFileName, format.Format);
                if (!resolved.IsSuccess
                    || !string.Equals(resolved.Path!.RelativePath, format.ExpectedRelativePath, StringComparison.Ordinal))
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
            progress?.Report(new(index + 1, demands.Length, cacheHits, inspections,
                $"{format.Format} front matter"));
        }
        return PrepareFinalAuthorReviewUseCase.Execute(
            state,
            initial.EmbeddedObservations,
            initial.MetadataCacheHitCount,
            initial.MetadataInspectionCount,
            observations,
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
