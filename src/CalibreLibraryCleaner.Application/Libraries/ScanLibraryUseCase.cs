using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed class ScanLibraryUseCase(
    ILibraryPathResolver pathResolver,
    ICalibreMetadataReader metadataReader,
    IClock clock)
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
        List<CalibreBook> books = [];
        long totalFormats = catalog.Books.Sum(book => (long)book.Formats.Count);
        long completedFormats = 0;
        progress?.Report(new(
            LibraryScanPhase.ResolvingFiles,
            completedFormats,
            totalFormats,
            "Resolving format files"));

        foreach (CalibreBookRecord bookRecord in catalog.Books.OrderBy(book => book.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibreBookId bookId = new(bookRecord.Id);
            List<BookFormat> formats = [];

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
                    formats.Add(new(
                        canonicalFormat,
                        formatRecord.StoredName,
                        string.Empty,
                        FormatFileStatus.InvalidPath));
                    findings.Add(CreateInvalidPathFinding(
                        bookId,
                        canonicalFormat,
                        bookRecord.RelativeDirectory,
                        resolved.Reason!));
                }
                else
                {
                    bool exists = await pathResolver
                        .FileExistsAsync(resolved.Path!, cancellationToken)
                        .ConfigureAwait(false);
                    formats.Add(new(
                        canonicalFormat,
                        formatRecord.StoredName,
                        resolved.Path!.RelativePath,
                        exists ? FormatFileStatus.Present : FormatFileStatus.Missing));

                    if (!exists)
                    {
                        findings.Add(CreateMissingFileFinding(
                            bookId,
                            canonicalFormat,
                            resolved.Path.RelativePath));
                    }
                }

                completedFormats++;
                ReportProgress(
                    progress,
                    LibraryScanPhase.ResolvingFiles,
                    completedFormats,
                    totalFormats,
                    "Resolving format files");
            }

            books.Add(new(
                bookId,
                bookRecord.Title,
                bookRecord.AuthorSort,
                bookRecord.Authors.Select(author => new BookAuthor(
                    new CalibreAuthorId(author.Id),
                    author.Name,
                    author.SortName)),
                bookRecord.Identifiers
                    .OrderBy(identifier => identifier.Type, StringComparer.Ordinal)
                    .ThenBy(identifier => identifier.Value, StringComparer.Ordinal)
                    .Select(identifier => new BookIdentifier(identifier.Type, identifier.Value)),
                formats,
                bookRecord.RelativeDirectory));
        }

        findings = findings
            .OrderBy(finding => finding.BookId?.Value ?? 0)
            .ThenBy(finding => finding.Format, StringComparer.Ordinal)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ToList();

        LibrarySnapshot snapshot = new(
            new LibraryIdentity(catalog.LibraryUuid, catalog.SchemaVersion, validation.Location!.LibraryRoot),
            clock.GetUtcNow(),
            books,
            findings);
        progress?.Report(new(LibraryScanPhase.Completed, 1, 1, "Scan complete"));
        return LibraryScanOutcome.Success(snapshot);
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

    private static LibraryFinding CreateMissingFileFinding(
        CalibreBookId bookId,
        string format,
        string relativePath) => new(
        "FORMAT_FILE_MISSING",
        FindingSeverity.Warning,
        $"The expected {format} file is missing.",
        "Use Calibre's Library maintenance tools or restore the file; this scan made no changes.",
        bookId,
        format,
        relativePath);

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
}
