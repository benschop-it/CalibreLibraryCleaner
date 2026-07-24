namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record BookAuthor
{
    public BookAuthor(CalibreAuthorId id, string name, string sortName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sortName);
        Id = id;
        Name = name;
        SortName = sortName;
    }

    public CalibreAuthorId Id { get; }

    public string Name { get; }

    public string SortName { get; }
}
