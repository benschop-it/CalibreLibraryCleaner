namespace CalibreLibraryCleaner.Infrastructure.Sqlite;

internal static class CalibreSchemaContract
{
    public const int SupportedVersion = 27;

    public static IReadOnlyDictionary<string, string[]> RequiredColumns { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["library_id"] = ["uuid"],
            ["books"] = ["id", "title", "author_sort", "path", "pubdate", "series_index", "has_cover"],
            ["authors"] = ["id", "name", "sort"],
            ["books_authors_link"] = ["id", "book", "author"],
            ["identifiers"] = ["book", "type", "val"],
            ["data"] = ["book", "format", "name"],
            ["publishers"] = ["id", "name"],
            ["books_publishers_link"] = ["id", "book", "publisher"],
            ["series"] = ["id", "name"],
            ["books_series_link"] = ["id", "book", "series"],
            ["languages"] = ["id", "lang_code"],
            ["books_languages_link"] = ["id", "book", "lang_code", "item_order"],
        };
}
