using System.Collections.ObjectModel;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record CalibreCatalogRecord
{
    public CalibreCatalogRecord(
        string libraryUuid,
        int schemaVersion,
        IEnumerable<CalibreBookRecord> books,
        IEnumerable<CalibreCatalogIssueRecord>? issues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        ArgumentNullException.ThrowIfNull(books);

        LibraryUuid = libraryUuid;
        SchemaVersion = schemaVersion;
        Books = new ReadOnlyCollection<CalibreBookRecord>(books.ToArray());
        Issues = new ReadOnlyCollection<CalibreCatalogIssueRecord>((issues ?? []).ToArray());
    }

    public string LibraryUuid { get; }

    public int SchemaVersion { get; }

    public IReadOnlyList<CalibreBookRecord> Books { get; }

    public IReadOnlyList<CalibreCatalogIssueRecord> Issues { get; }
}

public sealed record CalibreBookRecord(
    long Id,
    string Title,
    string AuthorSort,
    string RelativeDirectory,
    IReadOnlyList<CalibreAuthorRecord> Authors,
    IReadOnlyList<CalibreIdentifierRecord> Identifiers,
    IReadOnlyList<CalibreFormatRecord> Formats,
    CalibrePublicationRecord? Publication = null);

public sealed record CalibrePublicationRecord(
    string? Publisher,
    DateTimeOffset? PublicationDate,
    string? Series,
    decimal? SeriesIndex,
    IReadOnlyList<string> Languages,
    bool HasCover);

public sealed record CalibreAuthorRecord(long Id, string Name, string SortName);

public sealed record CalibreIdentifierRecord(string Type, string Value);

public sealed record CalibreFormatRecord(string Format, string StoredName);

public sealed record CalibreCatalogIssueRecord(
    string Code,
    string Message,
    string SuggestedAction,
    long BookId,
    string? Format = null,
    string? RelativePath = null);
