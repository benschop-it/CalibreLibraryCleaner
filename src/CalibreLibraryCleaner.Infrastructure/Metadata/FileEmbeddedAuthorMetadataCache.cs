using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Metadata;

internal sealed class FileEmbeddedAuthorMetadataCache(EmbeddedAuthorMetadataCacheOptions options) :
    IEmbeddedAuthorMetadataCache
{
    private const string SchemaVersion = "embedded-author-metadata-cache/1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
    };

    public async Task<EmbeddedAuthorMetadataObservation?> TryReadAsync(
        EmbeddedAuthorMetadataCacheKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(StorageRoot(), FileName(key));
        if (!File.Exists(path)
            || !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return null;
        try
        {
            FileInfo info = new(path);
            if (info.Length is <= 0 || info.Length > options.MaximumEntryBytes) return null;
            byte[] bytes = new byte[checked((int)info.Length)];
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            CacheDocument document = JsonSerializer.Deserialize<CacheDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The embedded author cache entry is empty.");
            if (document.SchemaVersion != SchemaVersion
                || document.Format != key.Format
                || document.SizeInBytes != key.Fingerprint.SizeInBytes
                || document.Sha256 != key.Fingerprint.Sha256.Value
                || document.ExtractorVersion != key.ExtractorVersion
                || document.LimitsVersion != key.LimitsVersion)
                return null;
            return new(
                key,
                document.Status,
                document.EmbeddedTitle,
                document.Authors,
                document.ProblemCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or InvalidDataException
                                           or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        EmbeddedAuthorMetadataObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        string root = StorageRoot();
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            root = StorageRoot();
        }
        string path = Path.Combine(root, FileName(observation.Key));
        if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _))
            throw new IOException("The embedded author cache entry is not a physical file.");
        CacheDocument document = new(
            SchemaVersion,
            observation.Key.Format,
            observation.Key.Fingerprint.SizeInBytes,
            observation.Key.Fingerprint.Sha256.Value,
            observation.Key.ExtractorVersion,
            observation.Key.LimitsVersion,
            observation.Status,
            observation.EmbeddedTitle,
            observation.Authors.ToArray(),
            observation.ProblemCode);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > options.MaximumEntryBytes)
            throw new InvalidDataException("The embedded author cache entry exceeds its bound.");
        string temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Prune(root, cancellationToken);
    }

    private void Prune(string root, CancellationToken cancellationToken)
    {
        FileInfo[] entries = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(value => new FileInfo(value))
            .Where(value => ExecutionPathGuard.TryRejectReparsePointLeaf(value.FullName, true, out _))
            .OrderBy(value => value.LastWriteTimeUtc)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();
        long total = entries.Sum(value => value.Length);
        int count = entries.Length;
        foreach (FileInfo entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count <= options.MaximumEntries && total <= options.MaximumTotalBytes) break;
            long length = entry.Length;
            entry.Delete();
            total -= length;
            count--;
        }
    }

    private string StorageRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, Directory.Exists(root), out _, out _))
            throw new IOException("The embedded author cache directory is not physical.");
        return root;
    }

    private static string FileName(EmbeddedAuthorMetadataCacheKey key)
    {
        string identity = string.Join('\u001F',
            key.Format,
            key.Fingerprint.SizeInBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key.Fingerprint.Sha256.Value,
            key.ExtractorVersion,
            key.LimitsVersion);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json";
    }

    private sealed record CacheDocument(
        string SchemaVersion,
        string Format,
        long SizeInBytes,
        string Sha256,
        string ExtractorVersion,
        string LimitsVersion,
        EmbeddedAuthorMetadataStatus Status,
        string? EmbeddedTitle,
        string[] Authors,
        string? ProblemCode);
}
