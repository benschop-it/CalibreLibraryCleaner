using Microsoft.Data.Sqlite;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;

internal sealed class SyntheticCalibreLibrary : IDisposable
{
    private readonly TemporaryDirectory _temporaryDirectory = new();

    public SyntheticCalibreLibrary(int schemaVersion = 27)
    {
        RootPath = _temporaryDirectory.Path;
        DatabasePath = Path.Combine(RootPath, "metadata.db");
        LibraryUuid = "87f7ed1f-59a8-45a6-975a-7e06fd84780d";
        using SqliteConnection connection = OpenWritable();
        Execute(connection, """
            CREATE TABLE library_id (id INTEGER PRIMARY KEY, uuid TEXT NOT NULL UNIQUE);
            CREATE TABLE books (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                author_sort TEXT,
                path TEXT NOT NULL,
                pubdate TIMESTAMP,
                series_index REAL NOT NULL DEFAULT 1.0,
                has_cover BOOL DEFAULT 0);
            CREATE TABLE authors (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                sort TEXT);
            CREATE TABLE books_authors_link (
                id INTEGER PRIMARY KEY,
                book INTEGER NOT NULL,
                author INTEGER NOT NULL,
                UNIQUE(book, author));
            CREATE TABLE identifiers (
                id INTEGER PRIMARY KEY,
                book INTEGER NOT NULL,
                type TEXT NOT NULL COLLATE NOCASE,
                val TEXT NOT NULL COLLATE NOCASE,
                UNIQUE(book, type));
            CREATE TABLE data (
                id INTEGER PRIMARY KEY,
                book INTEGER NOT NULL,
                format TEXT NOT NULL COLLATE NOCASE,
                name TEXT NOT NULL,
                UNIQUE(book, format));
            CREATE TABLE publishers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE books_publishers_link (
                id INTEGER PRIMARY KEY, book INTEGER NOT NULL, publisher INTEGER NOT NULL, UNIQUE(book));
            CREATE TABLE series (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE books_series_link (
                id INTEGER PRIMARY KEY, book INTEGER NOT NULL, series INTEGER NOT NULL, UNIQUE(book));
            CREATE TABLE languages (id INTEGER PRIMARY KEY, lang_code TEXT NOT NULL);
            CREATE TABLE books_languages_link (
                id INTEGER PRIMARY KEY,
                book INTEGER NOT NULL,
                lang_code INTEGER NOT NULL,
                item_order INTEGER NOT NULL DEFAULT 0,
                UNIQUE(book, lang_code));
            """);
        Execute(connection, $"PRAGMA user_version={schemaVersion};");
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library_id(id, uuid) VALUES (1, $uuid);";
        command.Parameters.AddWithValue("$uuid", LibraryUuid);
        command.ExecuteNonQuery();
    }

    public string RootPath { get; }

    public string DatabasePath { get; }

    public string LibraryUuid { get; }

    public string AddBook(bool createFormatFile = true, string relativeDirectory = "Author/Book (1)")
    {
        using SqliteConnection connection = OpenWritable();
        Execute(connection, """
            INSERT INTO books(id, title, author_sort, path)
            VALUES (1, 'Book', 'Author', 'Author/Book (1)');
            INSERT INTO authors(id, name, sort) VALUES (1, 'First Author', 'Author, First');
            INSERT INTO authors(id, name, sort) VALUES (2, 'Second Author', 'Author, Second');
            INSERT INTO books_authors_link(id, book, author) VALUES (1, 1, 2);
            INSERT INTO books_authors_link(id, book, author) VALUES (2, 1, 1);
            INSERT INTO identifiers(id, book, type, val) VALUES (1, 1, 'isbn', '9780000000001');
            INSERT INTO identifiers(id, book, type, val) VALUES (2, 1, 'asin', 'B000TEST');
            INSERT INTO data(id, book, format, name) VALUES (1, 1, 'EPUB', 'Book');
            """);
        if (!string.Equals(relativeDirectory, "Author/Book (1)", StringComparison.Ordinal))
        {
            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE books SET path = $path WHERE id = 1;";
            update.Parameters.AddWithValue("$path", relativeDirectory);
            update.ExecuteNonQuery();
        }

        string directory = Path.Combine(RootPath, "Author", "Book (1)");
        string formatPath = Path.Combine(directory, "Book.epub");
        if (createFormatFile)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(formatPath, [0x50, 0x4B, 0x03, 0x04]);
        }

        return formatPath;
    }

    public string AddSimpleBook(int id, byte[]? content, string format = "EPUB")
    {
        return AddMetadataBook(
            id,
            $"Book {id}",
            [$"Author {id}"],
            [$"Author {id}"],
            content,
            format);
    }

    public string AddMetadataBook(
        int id,
        string title,
        IReadOnlyList<string> authorNames,
        IReadOnlyList<string> authorSortNames,
        byte[]? content,
        string format = "EPUB",
        string? identifier = null)
    {
        ArgumentNullException.ThrowIfNull(authorNames);
        ArgumentNullException.ThrowIfNull(authorSortNames);
        if (authorNames.Count != authorSortNames.Count)
        {
            throw new ArgumentException("Author names and sort names must have the same count.", nameof(authorSortNames));
        }

        string relativeDirectory = $"Author {id}/Book ({id})";
        string storedName = $"Book {id}";
        using SqliteConnection connection = OpenWritable();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO books(id, title, author_sort, path) VALUES ($id, $title, $authorSort, $path);
            INSERT INTO data(id, book, format, name) VALUES ($id, $id, $format, $storedName);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$authorSort", string.Join(" & ", authorSortNames));
        command.Parameters.AddWithValue("$path", relativeDirectory);
        command.Parameters.AddWithValue("$format", format);
        command.Parameters.AddWithValue("$storedName", storedName);
        command.ExecuteNonQuery();

        for (int index = 0; index < authorNames.Count; index++)
        {
            using SqliteCommand authorCommand = connection.CreateCommand();
            authorCommand.CommandText = """
                INSERT INTO authors(id, name, sort) VALUES ($authorId, $name, $sort);
                INSERT INTO books_authors_link(id, book, author) VALUES ($linkId, $bookId, $authorId);
                """;
            authorCommand.Parameters.AddWithValue("$authorId", (id * 1000) + index + 1);
            authorCommand.Parameters.AddWithValue("$linkId", (id * 1000) + index + 1);
            authorCommand.Parameters.AddWithValue("$bookId", id);
            authorCommand.Parameters.AddWithValue("$name", authorNames[index]);
            authorCommand.Parameters.AddWithValue("$sort", authorSortNames[index]);
            authorCommand.ExecuteNonQuery();
        }

        if (identifier is not null)
        {
            using SqliteCommand identifierCommand = connection.CreateCommand();
            identifierCommand.CommandText =
                "INSERT INTO identifiers(id, book, type, val) VALUES ($id, $book, 'isbn', $value);";
            identifierCommand.Parameters.AddWithValue("$id", id);
            identifierCommand.Parameters.AddWithValue("$book", id);
            identifierCommand.Parameters.AddWithValue("$value", identifier);
            identifierCommand.ExecuteNonQuery();
        }

        string directory = Path.Combine(RootPath, $"Author {id}", $"Book ({id})");
        string path = Path.Combine(directory, $"{storedName}.{format.ToLowerInvariant()}");
        if (content is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, content);
        }

        return path;
    }

    public void AddBrokenAuthorLink(int bookId, int linkId, int missingAuthorId)
    {
        using SqliteConnection connection = OpenWritable();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO books_authors_link(id, book, author) VALUES ($linkId, $bookId, $authorId);";
        command.Parameters.AddWithValue("$linkId", linkId);
        command.Parameters.AddWithValue("$bookId", bookId);
        command.Parameters.AddWithValue("$authorId", missingAuthorId);
        command.ExecuteNonQuery();
    }

    public void SetPublicationMetadata(
        int bookId,
        string? publisher = null,
        string? publicationDate = null,
        string? series = null,
        decimal seriesIndex = 1,
        IReadOnlyList<string>? languages = null,
        bool hasCover = false)
    {
        using SqliteConnection connection = OpenWritable();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE books SET pubdate = $pubdate, series_index = $seriesIndex, has_cover = $hasCover WHERE id = $bookId;";
            command.Parameters.AddWithValue("$pubdate", (object?)publicationDate ?? DBNull.Value);
            command.Parameters.AddWithValue("$seriesIndex", seriesIndex);
            command.Parameters.AddWithValue("$hasCover", hasCover ? 1 : 0);
            command.Parameters.AddWithValue("$bookId", bookId);
            command.ExecuteNonQuery();
        }

        if (publisher is not null)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO publishers(id, name) VALUES ($id, $name); INSERT INTO books_publishers_link(id, book, publisher) VALUES ($id, $book, $id);";
            command.Parameters.AddWithValue("$id", 10_000 + bookId);
            command.Parameters.AddWithValue("$book", bookId);
            command.Parameters.AddWithValue("$name", publisher);
            command.ExecuteNonQuery();
        }

        if (series is not null)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO series(id, name) VALUES ($id, $name); INSERT INTO books_series_link(id, book, series) VALUES ($id, $book, $id);";
            command.Parameters.AddWithValue("$id", 20_000 + bookId);
            command.Parameters.AddWithValue("$book", bookId);
            command.Parameters.AddWithValue("$name", series);
            command.ExecuteNonQuery();
        }

        int order = 0;
        foreach (string language in languages ?? [])
        {
            using SqliteCommand command = connection.CreateCommand();
            int id = 30_000 + (bookId * 100) + order;
            command.CommandText = "INSERT INTO languages(id, lang_code) VALUES ($id, $language); INSERT INTO books_languages_link(id, book, lang_code, item_order) VALUES ($id, $book, $id, $order);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$book", bookId);
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$order", order);
            command.ExecuteNonQuery();
            order++;
        }
    }

    public void DropRequiredTable(string table)
    {
        using SqliteConnection connection = OpenWritable();
        Execute(connection, $"DROP TABLE {table};");
    }

    public void Dispose() => _temporaryDirectory.Dispose();

    private SqliteConnection OpenWritable()
    {
        SqliteConnection connection = new($"Data Source={DatabasePath};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
