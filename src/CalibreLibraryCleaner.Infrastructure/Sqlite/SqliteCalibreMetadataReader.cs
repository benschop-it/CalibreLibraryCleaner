using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CalibreLibraryCleaner.Infrastructure.Sqlite;

internal sealed class SqliteCalibreMetadataReader(
    ILogger<SqliteCalibreMetadataReader> logger) : ICalibreMetadataReader
{
    private static readonly Action<ILogger, int, int, int, Exception?> CatalogRead =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information,
            new EventId(1, nameof(CatalogRead)),
            "Read Calibre schema {SchemaVersion} with {BookCount} books and {FormatCount} formats");

    private static readonly Action<ILogger, int, LibraryErrorCode, Exception?> SqliteReadFailed =
        LoggerMessage.Define<int, LibraryErrorCode>(
            LogLevel.Warning,
            new EventId(2, nameof(SqliteReadFailed)),
            "Calibre database read failed with SQLite code {SqliteErrorCode} and error {ErrorCode}");

    private static readonly Action<ILogger, Exception?> CatalogInconsistent =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3, nameof(CatalogInconsistent)),
            "Calibre catalog data was inconsistent");

    private static readonly Action<ILogger, string, Exception?> DatabaseUnreadable =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(DatabaseUnreadable)),
            "Calibre metadata database could not be read: {ExceptionType}");

    private static readonly Action<ILogger, Exception?> UnexpectedReadFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(5, nameof(UnexpectedReadFailure)),
            "Unexpected failure while reading Calibre metadata");

    public Task<CalibreCatalogReadOutcome> ReadAsync(
        ValidatedLibraryLocation library,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        return Task.Run(
            () => ReadCore(library, progress, cancellationToken),
            cancellationToken);
    }

    private CalibreCatalogReadOutcome ReadCore(
        ValidatedLibraryLocation library,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report(new(
                LibraryScanPhase.OpeningDatabase,
                0,
                null,
                "Opening metadata.db read-only"));
            using SqliteConnection connection = ReadOnlySqliteConnectionFactory.Open(
                library.MetadataDatabasePath,
                cancellationToken);

            progress?.Report(new(
                LibraryScanPhase.ValidatingSchema,
                0,
                null,
                "Validating Calibre schema"));
            SchemaInspectionOutcome schema = CalibreSchemaInspector.Inspect(connection, cancellationToken);
            if (!schema.IsSuccess)
            {
                return CalibreCatalogReadOutcome.Failure(schema.Error!);
            }

            List<CalibreCatalogIssueRecord> issues = [];
            string libraryUuid = ReadLibraryUuid(connection, cancellationToken);
            Dictionary<long, MutableBookRecord> books = ReadBooks(connection, issues, progress, cancellationToken);
            foreach (MutableBookRecord book in books.Values.Where(book => string.IsNullOrWhiteSpace(book.AuthorSort)))
            {
                issues.Add(new(
                    "CATALOG_VALUE_INVALID",
                    "A book has a blank stored author-sort value.",
                    "Review this book in Calibre and recalculate author sort values if needed.",
                    book.Id));
            }

            ReadAuthors(connection, books, issues, progress, cancellationToken);
            ReadIdentifiers(connection, books, issues, progress, cancellationToken);
            ReadPublicationMetadata(connection, books, issues, progress, cancellationToken);
            ReadFormats(connection, books, progress, cancellationToken);

            CalibreBookRecord[] records = books.Values
                .OrderBy(book => book.Id)
                .Select(book => book.ToRecord())
                .ToArray();
            int formatCount = records.Sum(book => book.Formats.Count);
            CatalogRead(logger, schema.SchemaVersion!.Value, records.Length, formatCount, null);
            return CalibreCatalogReadOutcome.Success(new(
                libraryUuid,
                schema.SchemaVersion!.Value,
                records,
                issues));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            LibraryError error = MapSqliteError(exception);
            SqliteReadFailed(logger, exception.SqliteErrorCode, error.Code, null);
            return CalibreCatalogReadOutcome.Failure(error);
        }
        catch (InvalidDataException)
        {
            CatalogInconsistent(logger, null);
            return CalibreCatalogReadOutcome.Failure(new(
                LibraryErrorCode.CorruptDatabase,
                "The Calibre database contains inconsistent catalog records.",
                "Run Calibre's Library maintenance tools and retry."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DatabaseUnreadable(logger, exception.GetType().Name, null);
            return CalibreCatalogReadOutcome.Failure(new(
                LibraryErrorCode.MetadataDatabaseNotReadable,
                "metadata.db cannot be read.",
                "Check file permissions, close tools maintaining the library, and try again."));
        }
        catch (Exception exception)
        {
            UnexpectedReadFailure(logger, exception);
            return CalibreCatalogReadOutcome.Failure(new(
                LibraryErrorCode.UnexpectedReadFailure,
                "The library scan failed unexpectedly.",
                "Retry after closing Calibre. If the problem continues, inspect the application log."));
        }
    }

    private static string ReadLibraryUuid(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT uuid FROM library_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        if (values.Count != 1 || !Guid.TryParse(values[0], out _))
        {
            throw new InvalidDataException("The library identity must contain exactly one UUID.");
        }

        return values[0];
    }

    private static Dictionary<long, MutableBookRecord> ReadBooks(
        SqliteConnection connection,
        List<CalibreCatalogIssueRecord> issues,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = Count(connection, "books");
        progress?.Report(new(LibraryScanPhase.ReadingBooks, 0, total, "Reading books"));
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, author_sort, path, pubdate, series_index, has_cover FROM books ORDER BY id;";
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<long, MutableBookRecord> books = [];
        long completed = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long id = reader.GetInt64(0);
            string title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            string authorSort = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            string path = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            DateTimeOffset? publicationDate = ReadOptionalDate(reader, 4, id, issues);
            decimal? seriesIndex = ReadOptionalDecimal(reader, 5, id, issues);
            bool hasCover = ReadCoverFlag(reader, 6, id, issues);
            if (id <= 0 || string.IsNullOrWhiteSpace(title) || !books.TryAdd(id, new(
                    id,
                    title,
                    authorSort,
                    path,
                    publicationDate,
                    seriesIndex,
                    hasCover)))
            {
                throw new InvalidDataException("A book row has an invalid or duplicate identity/title.");
            }

            completed++;
            ReportRowProgress(progress, LibraryScanPhase.ReadingBooks, completed, total, "Reading books");
        }

        return books;
    }

    private static DateTimeOffset? ReadOptionalDate(
        SqliteDataReader reader,
        int ordinal,
        long bookId,
        List<CalibreCatalogIssueRecord> issues)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string value = Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset result))
        {
            return result;
        }

        issues.Add(InvalidOptionalValue(bookId, "publication date"));
        return null;
    }

    private static decimal? ReadOptionalDecimal(
        SqliteDataReader reader,
        int ordinal,
        long bookId,
        List<CalibreCatalogIssueRecord> issues)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            decimal value = Convert.ToDecimal(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
            if (value >= 0)
            {
                return value;
            }
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
        }

        issues.Add(InvalidOptionalValue(bookId, "series index"));
        return null;
    }

    private static bool ReadCoverFlag(
        SqliteDataReader reader,
        int ordinal,
        long bookId,
        List<CalibreCatalogIssueRecord> issues)
    {
        if (!reader.IsDBNull(ordinal))
        {
            try
            {
                long value = Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
                if (value is 0 or 1)
                {
                    return value == 1;
                }
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
            }
        }

        issues.Add(InvalidOptionalValue(bookId, "cover-availability flag"));
        return false;
    }

    private static CalibreCatalogIssueRecord InvalidOptionalValue(long bookId, string field) => new(
        "CATALOG_VALUE_INVALID",
        $"A book has an invalid stored {field} value.",
        "Review the stored metadata in Calibre.",
        bookId);

    private static void ReadAuthors(
        SqliteConnection connection,
        IReadOnlyDictionary<long, MutableBookRecord> books,
        List<CalibreCatalogIssueRecord> issues,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = Count(connection, "books_authors_link");
        progress?.Report(new(LibraryScanPhase.ReadingAuthors, 0, total, "Reading authors"));
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT link.book, author.id, author.name, author.sort
            FROM books_authors_link AS link
            LEFT JOIN authors AS author ON author.id = link.author
            ORDER BY link.id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        long completed = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bookId = reader.GetInt64(0);
            if (!books.TryGetValue(bookId, out MutableBookRecord? book))
            {
                throw new InvalidDataException("An author link references a missing book.");
            }

            if (reader.IsDBNull(1) || reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)))
            {
                issues.Add(new(
                    "AUTHOR_REFERENCE_MISSING",
                    "An author link references a missing or invalid author.",
                    "Use Calibre's Library maintenance tools to inspect this book.",
                    bookId));
            }
            else
            {
                long authorId = reader.GetInt64(1);
                string name = reader.GetString(2);
                string sortName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                book.Authors.Add(new(authorId, name, sortName));
                if (string.IsNullOrWhiteSpace(sortName))
                {
                    issues.Add(new(
                        "CATALOG_VALUE_INVALID",
                        "An author has a blank stored sort value.",
                        "Review the author in Calibre and recalculate author sort values if needed.",
                        bookId));
                }
            }

            completed++;
            ReportRowProgress(progress, LibraryScanPhase.ReadingAuthors, completed, total, "Reading authors");
        }
    }

    private static void ReadIdentifiers(
        SqliteConnection connection,
        IReadOnlyDictionary<long, MutableBookRecord> books,
        List<CalibreCatalogIssueRecord> issues,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = Count(connection, "identifiers");
        progress?.Report(new(LibraryScanPhase.ReadingIdentifiers, 0, total, "Reading identifiers"));
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT book, type, val FROM identifiers ORDER BY book, type, val;";
        using SqliteDataReader reader = command.ExecuteReader();
        long completed = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bookId = reader.GetInt64(0);
            if (!books.TryGetValue(bookId, out MutableBookRecord? book))
            {
                throw new InvalidDataException("An identifier references a missing book.");
            }

            string type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            string value = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value))
            {
                issues.Add(new(
                    "CATALOG_VALUE_INVALID",
                    "A stored identifier has a blank type or value and was not loaded.",
                    "Review this book's identifiers in Calibre.",
                    bookId));
            }
            else
            {
                book.Identifiers.Add(new(type, value));
            }

            completed++;
            ReportRowProgress(progress, LibraryScanPhase.ReadingIdentifiers, completed, total, "Reading identifiers");
        }
    }

    private static void ReadFormats(
        SqliteConnection connection,
        IReadOnlyDictionary<long, MutableBookRecord> books,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = Count(connection, "data");
        progress?.Report(new(LibraryScanPhase.ReadingFormats, 0, total, "Reading formats"));
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT book, format, name FROM data ORDER BY book, format, name;";
        using SqliteDataReader reader = command.ExecuteReader();
        long completed = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bookId = reader.GetInt64(0);
            if (!books.TryGetValue(bookId, out MutableBookRecord? book))
            {
                throw new InvalidDataException("A format references a missing book.");
            }

            string format = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            string storedName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(format))
            {
                throw new InvalidDataException("A format has a blank extension token.");
            }

            if (book.Formats.Any(existing =>
                    string.Equals(existing.Format, format, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("A book has duplicate format records.");
            }

            book.Formats.Add(new(format, storedName));
            completed++;
            ReportRowProgress(progress, LibraryScanPhase.ReadingFormats, completed, total, "Reading formats");
        }
    }

    private static void ReadPublicationMetadata(
        SqliteConnection connection,
        IReadOnlyDictionary<long, MutableBookRecord> books,
        List<CalibreCatalogIssueRecord> issues,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = Count(connection, "books_publishers_link")
            + Count(connection, "books_series_link")
            + Count(connection, "books_languages_link");
        progress?.Report(new(
            LibraryScanPhase.ReadingPublicationMetadata,
            0,
            total,
            "Reading publication metadata"));
        long completed = 0;
        ReadSingleValueLinks(
            connection,
            books,
            issues,
            """
            SELECT link.book, value.id, value.name
            FROM books_publishers_link AS link
            LEFT JOIN publishers AS value ON value.id = link.publisher
            ORDER BY link.id;
            """,
            "publisher",
            static (book, value) => book.Publisher = value,
            ref completed,
            total,
            progress,
            cancellationToken);
        ReadSingleValueLinks(
            connection,
            books,
            issues,
            """
            SELECT link.book, value.id, value.name
            FROM books_series_link AS link
            LEFT JOIN series AS value ON value.id = link.series
            ORDER BY link.id;
            """,
            "series",
            static (book, value) => book.Series = value,
            ref completed,
            total,
            progress,
            cancellationToken);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT link.book, language.id, language.lang_code
            FROM books_languages_link AS link
            LEFT JOIN languages AS language ON language.id = link.lang_code
            ORDER BY link.book, link.item_order, link.id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bookId = reader.GetInt64(0);
            if (!books.TryGetValue(bookId, out MutableBookRecord? book))
            {
                throw new InvalidDataException("A language link references a missing book.");
            }

            if (reader.IsDBNull(1) || reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)))
            {
                issues.Add(new(
                    "CATALOG_VALUE_INVALID",
                    "A language link references a missing or invalid language.",
                    "Use Calibre's Library maintenance tools to inspect this book.",
                    bookId));
            }
            else
            {
                string language = reader.GetString(2);
                if (book.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A book has duplicate language links.");
                }

                book.Languages.Add(language);
            }

            completed++;
            ReportRowProgress(progress, LibraryScanPhase.ReadingPublicationMetadata, completed, total, "Reading publication metadata");
        }
    }

    private static void ReadSingleValueLinks(
        SqliteConnection connection,
        IReadOnlyDictionary<long, MutableBookRecord> books,
        List<CalibreCatalogIssueRecord> issues,
        string sql,
        string fieldName,
        Action<MutableBookRecord, string> assign,
        ref long completed,
        long total,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<long> linkedBooks = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bookId = reader.GetInt64(0);
            if (!books.TryGetValue(bookId, out MutableBookRecord? book))
            {
                throw new InvalidDataException($"A {fieldName} link references a missing book.");
            }

            if (!linkedBooks.Add(bookId))
            {
                throw new InvalidDataException($"A book has ambiguous {fieldName} links.");
            }

            if (reader.IsDBNull(1) || reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)))
            {
                issues.Add(new(
                    "CATALOG_VALUE_INVALID",
                    $"A {fieldName} link references a missing or invalid value.",
                    "Use Calibre's Library maintenance tools to inspect this book.",
                    bookId));
            }
            else
            {
                assign(book, reader.GetString(2));
            }

            completed++;
            ReportRowProgress(progress, LibraryScanPhase.ReadingPublicationMetadata, completed, total, "Reading publication metadata");
        }
    }

    private static long Count(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ReportRowProgress(
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

    private static LibraryError MapSqliteError(SqliteException exception) => exception.SqliteErrorCode switch
    {
        5 or 6 => new(
            LibraryErrorCode.DatabaseBusy,
            "The Calibre database is busy or changing.",
            "Wait for Calibre library maintenance to finish, close other tools using the library, and retry."),
        11 => new(
            LibraryErrorCode.CorruptDatabase,
            "The Calibre database is malformed or corrupt.",
            "Run Calibre's Library maintenance tools and retry."),
        14 => new(
            LibraryErrorCode.MetadataDatabaseNotReadable,
            "metadata.db cannot be opened read-only.",
            "Check file permissions, close tools maintaining the library, and try again."),
        26 => new(
            LibraryErrorCode.NotSqliteDatabase,
            "metadata.db is not a valid SQLite database.",
            "Choose a valid Calibre library folder or restore metadata.db with Calibre."),
        _ => new(
            LibraryErrorCode.UnexpectedReadFailure,
            "The Calibre database could not be read.",
            "Close Calibre, retry the scan, and inspect the application log if the problem continues."),
    };

    private sealed class MutableBookRecord(
        long id,
        string title,
        string authorSort,
        string relativeDirectory,
        DateTimeOffset? publicationDate,
        decimal? seriesIndex,
        bool hasCover)
    {
        public long Id { get; } = id;

        public string Title { get; } = title;

        public string AuthorSort { get; } = authorSort;

        public string RelativeDirectory { get; } = relativeDirectory;

        public List<CalibreAuthorRecord> Authors { get; } = [];

        public List<CalibreIdentifierRecord> Identifiers { get; } = [];

        public List<CalibreFormatRecord> Formats { get; } = [];

        public string? Publisher { get; set; }

        public DateTimeOffset? PublicationDate { get; } = publicationDate;

        public string? Series { get; set; }

        public decimal? SeriesIndex { get; } = seriesIndex;

        public List<string> Languages { get; } = [];

        public bool HasCover { get; } = hasCover;

        public CalibreBookRecord ToRecord() => new(
            Id,
            Title,
            AuthorSort,
            RelativeDirectory,
            Authors.AsReadOnly(),
            Identifiers.AsReadOnly(),
            Formats.AsReadOnly(),
            new(
                Publisher,
                PublicationDate,
                Series,
                SeriesIndex,
                Languages.AsReadOnly(),
                HasCover));
    }
}
