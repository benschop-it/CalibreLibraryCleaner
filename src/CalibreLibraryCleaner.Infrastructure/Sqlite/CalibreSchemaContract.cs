namespace CalibreLibraryCleaner.Infrastructure.Sqlite;

internal static class CalibreSchemaContract
{
    public const int SupportedVersion = 26;

    public static IReadOnlyDictionary<string, string[]> RequiredColumns { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["library_id"] = ["uuid"],
            ["books"] = ["id", "title", "author_sort", "path"],
            ["authors"] = ["id", "name", "sort"],
            ["books_authors_link"] = ["id", "book", "author"],
            ["identifiers"] = ["book", "type", "val"],
            ["data"] = ["book", "format", "name"],
        };
}
