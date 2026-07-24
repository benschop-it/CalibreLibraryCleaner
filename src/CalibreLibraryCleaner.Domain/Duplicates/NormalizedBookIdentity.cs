namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record NormalizedBookIdentity
{
    public NormalizedBookIdentity(NormalizedTitle title, NormalizedAuthorSet authors)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(authors);
        Title = title;
        Authors = authors;
    }

    public NormalizedTitle Title { get; }

    public NormalizedAuthorSet Authors { get; }
}
