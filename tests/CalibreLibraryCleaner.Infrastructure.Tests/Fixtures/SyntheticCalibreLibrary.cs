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
                path TEXT NOT NULL);
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
        string relativeDirectory = $"Author {id}/Book ({id})";
        string storedName = $"Book {id}";
        using SqliteConnection connection = OpenWritable();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO books(id, title, author_sort, path) VALUES ($id, $title, $authorSort, $path);
            INSERT INTO authors(id, name, sort) VALUES ($authorId, $author, $authorSort);
            INSERT INTO books_authors_link(id, book, author) VALUES ($id, $id, $authorId);
            INSERT INTO data(id, book, format, name) VALUES ($id, $id, $format, $storedName);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", $"Book {id}");
        command.Parameters.AddWithValue("$authorId", 1000 + id);
        command.Parameters.AddWithValue("$author", $"Author {id}");
        command.Parameters.AddWithValue("$authorSort", $"Author {id}");
        command.Parameters.AddWithValue("$path", relativeDirectory);
        command.Parameters.AddWithValue("$format", format);
        command.Parameters.AddWithValue("$storedName", storedName);
        command.ExecuteNonQuery();

        string directory = Path.Combine(RootPath, $"Author {id}", $"Book ({id})");
        string path = Path.Combine(directory, $"{storedName}.{format.ToLowerInvariant()}");
        if (content is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, content);
        }

        return path;
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
