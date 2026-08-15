using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Caches;

internal sealed class FileFormatHashCache(FormatHashCacheOptions options) : IFormatHashCache
{
    private const string SchemaVersion = "format-hash-cache/1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
    };

    public async Task<FormatHashCacheEntry?> TryReadAsync(
        FormatHashCacheKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = GetStorageRoot();
            string path = Path.Combine(root, key.Value + ".json");
            if (!File.Exists(path) || !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return null;
            FileInfo info = new(path);
            if (info.Length is <= 0 || info.Length > options.MaximumEntryBytes) return null;
            byte[] bytes = new byte[checked((int)info.Length)];
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 8 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(bytes, JsonOptions);
            if (document is null
                || document.SchemaVersion != SchemaVersion
                || document.Key != key.Value
                || document.PolicyVersion != FormatHashCacheKey.PolicyVersion
                || document.FingerprintSize != document.ObservationLength)
                return null;
            FormatHashCacheEntry entry = new(
                key,
                document.PolicyVersion,
                new(document.FingerprintSize, new(document.FingerprintSha256)),
                new(
                    document.ObservationLength,
                    document.CreationTimeUtc,
                    document.LastWriteTimeUtc,
                    document.Attributes),
                document.VerifiedAtUtc.ToUniversalTime());
            return entry.Matches(key, entry.Observation) ? entry : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public async Task WriteAsync(FormatHashCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (!entry.Matches(entry.Key, entry.Observation))
            throw new ArgumentException("The hash cache entry is internally inconsistent.", nameof(entry));
        try
        {
            string root = GetStorageRoot();
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                root = GetStorageRoot();
            }
            string path = Path.Combine(root, entry.Key.Value + ".json");
            if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return;
            CacheDocument document = new(
                SchemaVersion,
                entry.Key.Value,
                entry.PolicyVersion,
                entry.Fingerprint.SizeInBytes,
                entry.Fingerprint.Sha256.Value,
                entry.Observation.Length,
                entry.Observation.CreationTimeUtc,
                entry.Observation.LastWriteTimeUtc,
                entry.Observation.Attributes,
                entry.VerifiedAtUtc.ToUniversalTime());
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.LongLength > options.MaximumEntryBytes) return;
            string temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or OverflowException)
        { }
    }

    public Task PruneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = GetStorageRoot();
            if (!Directory.Exists(root)) return Task.CompletedTask;
            FileInfo[] entries = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(value => ExecutionPathGuard.TryRejectReparsePointLeaf(value.FullName, true, out _))
                .OrderBy(value => value.LastWriteTimeUtc)
                .ThenBy(value => value.Name, StringComparer.Ordinal)
                .ToArray();
            long total = entries.Sum(value => value.Length);
            foreach (FileInfo entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= options.MaximumTotalBytes) break;
                long length = entry.Length;
                entry.Delete();
                total -= length;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or OverflowException)
        { }
        return Task.CompletedTask;
    }

    private string GetStorageRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        bool exists = Directory.Exists(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, exists, out _))
            throw new IOException("The format hash cache directory is not physical.");
        return root;
    }

    private sealed record CacheDocument(
        string SchemaVersion,
        string Key,
        string PolicyVersion,
        long FingerprintSize,
        string FingerprintSha256,
        long ObservationLength,
        DateTimeOffset CreationTimeUtc,
        DateTimeOffset LastWriteTimeUtc,
        int Attributes,
        DateTimeOffset VerifiedAtUtc);
}
