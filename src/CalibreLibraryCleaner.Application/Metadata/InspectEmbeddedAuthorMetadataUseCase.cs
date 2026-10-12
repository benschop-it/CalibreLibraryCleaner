using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record EmbeddedAuthorMetadataProgress(
    int CompletedFormats,
    int TotalFormats,
    int CacheHits,
    int Inspections,
    string Stage);

public sealed class InspectEmbeddedAuthorMetadataUseCase(
    ILibraryPathResolver pathResolver,
    IEpubInspector epubInspector,
    IPdfAuthorMetadataInspector pdfInspector,
    IEmbeddedAuthorMetadataCache cache)
{
    public async Task<FinalAuthorReviewWorkspace> ExecuteAsync(
        LibraryState state,
        FinalAuthorReviewWorkspace initial,
        IProgress<EmbeddedAuthorMetadataProgress>? progress,
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
                .Where(format => format.Format == "EPUB"
                    || format.Format == "PDF")
                .Where(format => format.FileStatus == FormatFileStatus.Present
                    && format.Fingerprint is not null
                    && format.Observation is not null)
                .Select(format => (Book: books[subject.BookId], Format: format)))
            .OrderBy(value => value.Book.Id.Value)
            .ThenBy(value => value.Format.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        List<EmbeddedAuthorMetadataObservation> observations = [];
        int cacheHits = 0;
        int inspections = 0;
        for (int index = 0; index < demands.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (CalibreBook book, BookFormat format) = demands[index];
            bool epub = format.Format == "EPUB";
            EmbeddedAuthorMetadataCacheKey key = new(
                format.Format,
                format.Fingerprint!,
                epub ? EmbeddedAuthorMetadataVersions.EpubExtractor : EmbeddedAuthorMetadataVersions.PdfExtractor,
                epub ? EmbeddedAuthorMetadataVersions.EpubLimits : EmbeddedAuthorMetadataVersions.PdfLimits);
            EmbeddedAuthorMetadataObservation? observation = await TryReadAsync(
                key, cancellationToken).ConfigureAwait(false);
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
                    observation = new(
                        key, EmbeddedAuthorMetadataStatus.Unavailable, null, [],
                        $"{format.Format}_AUTHOR_METADATA.UNSAFE_PATH");
                }
                else
                {
                    inspections++;
                    observation = epub
                        ? await epubInspector.InspectAuthorMetadataAsync(
                            new(
                                book.Id,
                                resolved.Path.LibraryRoot,
                                resolved.Path.FullPath,
                                resolved.Path.RelativePath,
                                format.Fingerprint!,
                                format.Observation!,
                                EpubInspectionLimits.V1),
                            cancellationToken).ConfigureAwait(false)
                        : await pdfInspector.InspectAuthorMetadataAsync(
                            new(
                                book.Id,
                                resolved.Path.LibraryRoot,
                                resolved.Path.FullPath,
                                resolved.Path.RelativePath,
                                format.Fingerprint!,
                                format.Observation!,
                                PdfInspectionLimits.V1),
                            cancellationToken).ConfigureAwait(false);
                }
                await TryWriteAsync(observation, cancellationToken).ConfigureAwait(false);
            }
            observations.Add(observation);
            progress?.Report(new(index + 1, demands.Length, cacheHits, inspections,
                $"{format.Format} metadata"));
        }
        return PrepareFinalAuthorReviewUseCase.Execute(
            state, observations, cacheHits, inspections);

        async Task<EmbeddedAuthorMetadataObservation?> TryReadAsync(
            EmbeddedAuthorMetadataCacheKey keyValue,
            CancellationToken token)
        {
            try { return await cache.TryReadAsync(keyValue, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsCacheFailure(exception)) { return null; }
        }

        async Task TryWriteAsync(EmbeddedAuthorMetadataObservation value, CancellationToken token)
        {
            try { await cache.WriteAsync(value, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsCacheFailure(exception)) { }
        }
    }

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
