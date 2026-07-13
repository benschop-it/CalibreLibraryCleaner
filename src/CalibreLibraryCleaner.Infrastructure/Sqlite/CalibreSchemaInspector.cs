using CalibreLibraryCleaner.Application.Libraries;
using Microsoft.Data.Sqlite;

namespace CalibreLibraryCleaner.Infrastructure.Sqlite;

internal static class CalibreSchemaInspector
{
    public static SchemaInspectionOutcome Inspect(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        int version = ReadVersion(connection);
        if (version != CalibreSchemaContract.SupportedVersion)
        {
            return SchemaInspectionOutcome.Failure(new(
                LibraryErrorCode.UnsupportedSchema,
                $"The Calibre library schema is not supported (schema {version}; expected {CalibreSchemaContract.SupportedVersion}).",
                "Open and update the library with Calibre 9.11, or update Calibre Library Cleaner."));
        }

        List<string> missing = [];
        foreach ((string table, string[] requiredColumns) in CalibreSchemaContract.RequiredColumns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> actualColumns = ReadColumns(connection, table);
            if (actualColumns.Count == 0)
            {
                missing.Add($"table {table}");
                continue;
            }

            missing.AddRange(requiredColumns
                .Where(column => !actualColumns.Contains(column))
                .Select(column => $"column {table}.{column}"));
        }

        return missing.Count == 0
            ? SchemaInspectionOutcome.Success(version)
            : SchemaInspectionOutcome.Failure(new(
                LibraryErrorCode.UnsupportedSchema,
                $"The Calibre library schema is missing required elements: {string.Join(", ", missing)}.",
                "Open the library with Calibre 9.11 and run Calibre's Library maintenance tools, then retry."));
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}

internal sealed record SchemaInspectionOutcome(int? SchemaVersion, LibraryError? Error)
{
    public bool IsSuccess => SchemaVersion is not null;

    public static SchemaInspectionOutcome Success(int version) => new(version, null);

    public static SchemaInspectionOutcome Failure(LibraryError error) => new(null, error);
}
