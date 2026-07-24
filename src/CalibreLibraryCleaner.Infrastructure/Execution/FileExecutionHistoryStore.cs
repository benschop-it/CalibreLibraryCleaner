using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Infrastructure.Calibre;

namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal sealed class FileExecutionHistoryStore(ExecutionStorageOptions options) : IExecutionHistoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public async Task<bool> HasRecoveryRequiredAsync(
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        string root = GetSafeRoot(libraryRoot);
        if (!Directory.Exists(root)) return false;
        foreach (string path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
                    return true;
                await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                ExecutionHistoryEntry? entry = await JsonSerializer.DeserializeAsync<ExecutionHistoryEntry>(
                    stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (entry is null) return true;
                if (string.Equals(entry.LibraryUuid, libraryUuid, StringComparison.Ordinal)
                    && entry.Disposition == CleanupExecutionDisposition.RecoveryRequired)
                    return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or JsonException or ArgumentException)
            {
                return true;
            }
        }
        return false;
    }

    public async Task RecordAsync(
        ExecutionHistoryEntry entry,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string root = GetSafeRoot(libraryRoot);
        Directory.CreateDirectory(root);
        root = GetSafeRoot(libraryRoot);
        string path = Path.Combine(root, $"{entry.ExecutionId}.json");
        if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
            throw new IOException("The execution history entry is not a physical file.");
        string temporary = Path.Combine(root, $".{entry.ExecutionId}.{Guid.NewGuid():N}.tmp");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, SerializerOptions);
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<IReadOnlyList<ExecutionHistoryEntry>> ReadAsync(
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        string root = GetSafeRoot(libraryRoot);
        if (!Directory.Exists(root)) return [];
        List<ExecutionHistoryEntry> entries = [];
        foreach (string path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _)) continue;
                await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                ExecutionHistoryEntry? entry = await JsonSerializer.DeserializeAsync<ExecutionHistoryEntry>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (entry is not null && string.Equals(entry.LibraryUuid, libraryUuid, StringComparison.Ordinal)) entries.Add(entry);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // A corrupt index entry is ignored; the execution bundle journal remains authoritative.
            }
        }
        return entries.OrderByDescending(value => value.FinishedAtUtc).ThenBy(value => value.ExecutionId.ToString(), StringComparer.Ordinal).ToArray();
    }

    private string GetSafeRoot(string libraryRoot)
    {
        string root = Path.GetFullPath(options.HistoryRoot);
        if (!ExecutionPathGuard.TryValidateExternalDirectory(
                libraryRoot, root, Directory.Exists(root), out string? verifiedRoot, out _))
            throw new IOException("The execution history root is not a physical external directory.");
        return verifiedRoot!;
    }
}
