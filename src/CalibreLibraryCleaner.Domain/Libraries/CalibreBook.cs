using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Executions;

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
        string relativeDirectory,
        BookPublicationMetadata? publicationMetadata = null,
        AppliedAuthorProvenance? appliedAuthorProvenance = null)
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
        PublicationMetadata = publicationMetadata ?? BookPublicationMetadata.Empty;
        if (appliedAuthorProvenance is not null
            && !appliedAuthorProvenance.Matches(Authors.Select(value => value.Name)))
            throw new ArgumentException(
                "Applied author provenance must bind to the exact current author values.",
                nameof(appliedAuthorProvenance));
        AppliedAuthorProvenance = appliedAuthorProvenance;
    }

    public CalibreBookId Id { get; }

    public string Title { get; }

    public string AuthorSort { get; }

    public IReadOnlyList<BookAuthor> Authors { get; }

    public IReadOnlyList<BookIdentifier> Identifiers { get; }

    public IReadOnlyList<BookFormat> Formats { get; }

    public string RelativeDirectory { get; }

    public BookPublicationMetadata PublicationMetadata { get; }

    public AppliedAuthorProvenance? AppliedAuthorProvenance { get; }
}
