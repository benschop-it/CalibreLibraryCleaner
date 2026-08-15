using System.Text.Json;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

internal sealed class FileBibliographicResolutionCache(BibliographicResolutionCacheOptions options) :
    IBibliographicResolutionCache
{
    private const string SchemaVersion = "open-library-resolution-cache/1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
    };

    public async Task<BibliographicWorkResolution?> TryReadAsync(
        BibliographicLookupQuery query,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = GetStorageRoot();
            string path = Path.Combine(root, query.QueryIdentity + ".json");
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
                || document.ProviderId != query.Provider.Id
                || document.ProviderVersion != query.Provider.Version
                || document.QueryPolicyVersion != BibliographicLookupQuery.PolicyVersion
                || document.ResolutionPolicyVersion != BibliographicWorkResolution.PolicyVersion
                || document.QueryIdentity != query.QueryIdentity
                || document.QueryFields != query.Fields
                || document.RetrievedAtUtc < minimumRetrievedAtUtc)
                return null;
            return new(
                query.BookId,
                query.Provider,
                document.QueryIdentity,
                document.QueryFields,
                document.RetrievedAtUtc,
                document.Status,
                document.WorkId,
                document.ProblemCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        BibliographicLookupQuery query,
        BibliographicWorkResolution resolution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(resolution);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Provider != resolution.Provider
            || query.QueryIdentity != resolution.QueryIdentity
            || query.Fields != resolution.QueryFields)
            throw new ArgumentException("The bibliographic cache query and resolution do not agree.");
        try
        {
            string root = GetStorageRoot();
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                root = GetStorageRoot();
            }
            string path = Path.Combine(root, query.QueryIdentity + ".json");
            if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return;
            CacheDocument document = new(
                SchemaVersion,
                query.Provider.Id,
                query.Provider.Version,
                BibliographicLookupQuery.PolicyVersion,
                BibliographicWorkResolution.PolicyVersion,
                query.QueryIdentity,
                query.Fields,
                resolution.RetrievedAtUtc,
                resolution.Status,
                resolution.WorkId,
                resolution.ProblemCode);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.LongLength > options.MaximumEntryBytes) return;
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            // Cache loss affects performance only.
        }
    }

    public Task PruneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = GetStorageRoot();
            if (!Directory.Exists(root)) return Task.CompletedTask;
            FileInfo[] entries = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(value => new FileInfo(value))
                .Where(value => ExecutionPathGuard.TryRejectReparsePointLeaf(value.FullName, true, out _))
                .OrderBy(value => value.LastWriteTimeUtc).ThenBy(value => value.Name, StringComparer.Ordinal)
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or OverflowException)
        {
            // Cache pruning is best effort.
        }
        return Task.CompletedTask;
    }

    private string GetStorageRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        bool exists = Directory.Exists(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, exists, out _))
            throw new IOException("The bibliographic cache directory is not physical.");
        return root;
    }

    private sealed record CacheDocument(
        string SchemaVersion,
        string ProviderId,
        string ProviderVersion,
        string QueryPolicyVersion,
        string ResolutionPolicyVersion,
        string QueryIdentity,
        BibliographicQueryFields QueryFields,
        DateTimeOffset RetrievedAtUtc,
        BibliographicResolutionStatus Status,
        string? WorkId,
        string? ProblemCode);
}
