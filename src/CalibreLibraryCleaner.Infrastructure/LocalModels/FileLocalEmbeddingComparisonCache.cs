using System.Text.Json;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.LocalModels;

public sealed record LocalEmbeddingComparisonCacheOptions
{
    public LocalEmbeddingComparisonCacheOptions(
        string? storageRoot = null,
        long maximumEntryBytes = 16 * 1024,
        long maximumTotalBytes = 64L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntryBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntryBytes, 128 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, maximumEntryBytes);
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "local-embedding-comparisons");
        MaximumEntryBytes = maximumEntryBytes;
        MaximumTotalBytes = maximumTotalBytes;
    }

    public string StorageRoot { get; }
    public long MaximumEntryBytes { get; }
    public long MaximumTotalBytes { get; }
}

internal sealed class FileLocalEmbeddingComparisonCache(LocalEmbeddingComparisonCacheOptions options) :
    ILocalEmbeddingComparisonCache
{
    private const string SchemaVersion = "local-embedding-comparison-cache/1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
    };

    public async Task<LocalEmbeddingComparison?> TryReadAsync(
        LocalEmbeddingComparisonCacheKey key,
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
            if (document is null || document.SchemaVersion != SchemaVersion
                || document.Key != key.Value
                || document.FirstBookId != key.PairId.First.Value
                || document.SecondBookId != key.PairId.Second.Value
                || document.RuntimeId != key.Model.RuntimeId
                || document.RuntimeVersion != key.Model.RuntimeVersion
                || document.ModelId != key.Model.ModelId
                || document.ModelVersion != key.Model.ModelVersion
                || document.Dimensions != key.Model.Dimensions
                || document.FirstInputIdentity != key.FirstInputIdentity
                || document.SecondInputIdentity != key.SecondInputIdentity
                || document.PolicyVersion != LocalEmbeddingComparison.PolicyVersion)
                return null;
            return new(
                key.PairId,
                key.Model,
                key.FirstInputIdentity,
                key.SecondInputIdentity,
                document.Status,
                document.SimilarityPermille,
                document.ProblemCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        LocalEmbeddingComparisonCacheKey key,
        LocalEmbeddingComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(comparison);
        cancellationToken.ThrowIfCancellationRequested();
        if (comparison.PairId != key.PairId || comparison.Model != key.Model
            || comparison.FirstInputIdentity != key.FirstInputIdentity
            || comparison.SecondInputIdentity != key.SecondInputIdentity)
            throw new ArgumentException("Embedding comparison does not match its cache key.");
        try
        {
            string root = GetStorageRoot();
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                root = GetStorageRoot();
            }
            string path = Path.Combine(root, key.Value + ".json");
            if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return;
            CacheDocument document = new(
                SchemaVersion,
                key.Value,
                key.PairId.First.Value,
                key.PairId.Second.Value,
                key.Model.RuntimeId,
                key.Model.RuntimeVersion,
                key.Model.ModelId,
                key.Model.ModelVersion,
                key.Model.Dimensions,
                key.FirstInputIdentity,
                key.SecondInputIdentity,
                LocalEmbeddingComparison.PolicyVersion,
                comparison.Status,
                comparison.SimilarityPermille,
                comparison.ProblemCode);
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
            throw new IOException("The local embedding cache directory is not physical.");
        return root;
    }

    private sealed record CacheDocument(
        string SchemaVersion,
        string Key,
        long FirstBookId,
        long SecondBookId,
        string RuntimeId,
        string RuntimeVersion,
        string ModelId,
        string ModelVersion,
        int Dimensions,
        string FirstInputIdentity,
        string SecondInputIdentity,
        string PolicyVersion,
        LocalEmbeddingComparisonStatus Status,
        int? SimilarityPermille,
        string? ProblemCode);
}
