using System.Collections.ObjectModel;

namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record CalibreBook
{
    public CalibreBook(
        CalibreBookId id,
        string title,
        string authorSort,
        IEnumerable<BookAuthor> authors,
        IEnumerable<BookIdentifier> identifiers,
        IEnumerable<BookFormat> formats,
        string relativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(authorSort);
        ArgumentNullException.ThrowIfNull(authors);
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        Id = id;
        Title = title;
        AuthorSort = authorSort;
        Authors = new ReadOnlyCollection<BookAuthor>(authors.ToArray());
        Identifiers = new ReadOnlyCollection<BookIdentifier>(identifiers.ToArray());
        Formats = new ReadOnlyCollection<BookFormat>(formats.ToArray());
        RelativeDirectory = relativeDirectory;
    }

    public CalibreBookId Id { get; }

    public string Title { get; }

    public string AuthorSort { get; }

    public IReadOnlyList<BookAuthor> Authors { get; }

    public IReadOnlyList<BookIdentifier> Identifiers { get; }

    public IReadOnlyList<BookFormat> Formats { get; }

    public string RelativeDirectory { get; }
}
