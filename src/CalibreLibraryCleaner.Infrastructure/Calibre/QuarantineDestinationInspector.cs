using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class QuarantineDestinationInspector : IQuarantineDestinationInspector
{
    public Task<QuarantineDestinationInspection> InspectAsync(
        string sourceLibraryRoot,
        string destinationLibraryRoot,
        CancellationToken cancellationToken) => Task.Run(
        () => Inspect(sourceLibraryRoot, destinationLibraryRoot, cancellationToken),
        cancellationToken);

    private static QuarantineDestinationInspection Inspect(
        string sourceLibraryRoot,
        string destinationLibraryRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExecutionPathGuard.TryValidateQuarantineDestination(
                sourceLibraryRoot,
                destinationLibraryRoot,
                out string? canonical,
                out bool exists,
                out _))
            return new(null, "QUARANTINE_DESTINATION.INVALID");
        if (!exists) return new(new(false, null));
        try
        {
            using SqliteConnection connection = ReadOnlySqliteConnectionFactory.Open(
                Path.Combine(canonical!, "metadata.db"),
                cancellationToken);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT uuid FROM library_id;";
            using SqliteDataReader reader = command.ExecuteReader();
            List<string> values = [];
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }
            return values.Count == 1 && Guid.TryParse(values[0], out _)
                ? new(new(true, values[0]))
                : new(null, "QUARANTINE_DESTINATION.IDENTITY_INVALID");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is SqliteException or IOException
                                           or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(null, "QUARANTINE_DESTINATION.UNREADABLE");
        }
    }
}
