using Microsoft.Data.Sqlite;

namespace CalibreLibraryCleaner.Infrastructure.Sqlite;

internal static class ReadOnlySqliteConnectionFactory
{
    public static SqliteConnection Open(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        SqliteConnection connection = new(builder.ConnectionString);

        try
        {
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA query_only;";
            object? queryOnly = command.ExecuteScalar();
            if (Convert.ToInt64(queryOnly, System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException("SQLite query-only mode could not be enabled.");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
