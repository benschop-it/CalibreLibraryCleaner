using System.Diagnostics;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Infrastructure.Epub;

internal sealed partial class FileEpubContentSignatureCache(
    EpubContentSignatureCacheOptions options,
    ILogger<FileEpubContentSignatureCache>? logger = null) :
    IEpubContentSignatureCache
{
    private const string SchemaVersion = "epub-content-signature-cache/1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
    };
    private readonly ILogger<FileEpubContentSignatureCache> _logger = logger
        ?? NullLogger<FileEpubContentSignatureCache>.Instance;

    public async Task<EpubContentSignature?> TryReadAsync(
        EpubContentSignatureCacheKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        string root = GetStorageRoot(out int rootSegmentsChecked);
        long rootValidated = Stopwatch.GetTimestamp();
        string path = Path.Combine(root, key.Value + ".json");
        if (!File.Exists(path))
        {
            LogCacheRead(_logger, false, 0, rootSegmentsChecked,
                ElapsedMilliseconds(started, rootValidated), 0, 0,
                ElapsedMilliseconds(started, Stopwatch.GetTimestamp()));
            return null;
        }
        if (!ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return null;
        try
        {
            FileInfo info = new(path);
            if (info.Length is <= 0 || info.Length > options.MaximumEntryBytes) return null;
            byte[] bytes = new byte[checked((int)info.Length)];
            long readStarted = Stopwatch.GetTimestamp();
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            long readCompleted = Stopwatch.GetTimestamp();
            CacheDocument document = JsonSerializer.Deserialize<CacheDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The signature cache entry is empty.");
            EpubContentSignature signature = Rehydrate(document, key);
            long completed = Stopwatch.GetTimestamp();
            LogCacheRead(_logger, true, bytes.LongLength, rootSegmentsChecked,
                ElapsedMilliseconds(started, rootValidated),
                ElapsedMilliseconds(readStarted, readCompleted),
                ElapsedMilliseconds(readCompleted, completed),
                ElapsedMilliseconds(started, completed));
            return signature;
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
        EpubContentSignatureCacheKey key,
        EpubContentSignature signature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(signature);
        cancellationToken.ThrowIfCancellationRequested();
        if (signature.Fingerprint != key.Fingerprint)
            throw new ArgumentException("The signature fingerprint does not match its cache key.", nameof(signature));
        long started = Stopwatch.GetTimestamp();
        string root = GetStorageRoot(out int initialRootSegmentsChecked);
        int createdRootSegmentsChecked = 0;
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            root = GetStorageRoot(out createdRootSegmentsChecked);
        }
        long rootValidated = Stopwatch.GetTimestamp();
        string path = Path.Combine(root, key.Value + ".json");
        if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _))
            throw new IOException("The signature cache entry is not a physical file.");
        long serializationStarted = Stopwatch.GetTimestamp();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(CreateDocument(key, signature), JsonOptions);
        long serializationCompleted = Stopwatch.GetTimestamp();
        if (bytes.LongLength > options.MaximumEntryBytes)
            throw new InvalidDataException("The signature cache entry exceeds its configured bound.");
        string temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
        long writeStarted = Stopwatch.GetTimestamp();
        try
        {
            await using (FileStream stream = new(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        long completed = Stopwatch.GetTimestamp();
        LogCacheWrite(_logger, bytes.LongLength,
            initialRootSegmentsChecked + createdRootSegmentsChecked,
            ElapsedMilliseconds(started, rootValidated),
            ElapsedMilliseconds(serializationStarted, serializationCompleted),
            ElapsedMilliseconds(writeStarted, completed),
            ElapsedMilliseconds(started, completed));
    }

    public Task PruneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        string root = GetStorageRoot(out int rootSegmentsChecked);
        if (!Directory.Exists(root)) return Task.CompletedTask;
        FileInfo[] entries = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(value => ExecutionPathGuard.TryRejectReparsePointLeaf(value.FullName, true, out _))
            .ToArray();
        long total = entries.Sum(value => value.Length);
        long bytesBefore = total;
        int removed = 0;
        if (total > options.MaximumTotalBytes)
        {
            foreach (FileInfo entry in entries
                .OrderBy(value => value.LastWriteTimeUtc)
                .ThenBy(value => value.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= options.MaximumTotalBytes) break;
                long length = entry.Length;
                entry.Delete();
                total -= length;
                removed++;
            }
        }
        LogCachePrune(_logger, entries.Length, removed, bytesBefore, total,
            rootSegmentsChecked, ElapsedMilliseconds(started, Stopwatch.GetTimestamp()));
        return Task.CompletedTask;
    }

    private string GetStorageRoot(out int segmentsChecked)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        bool exists = Directory.Exists(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(
                root, exists, out _, out ExecutionPathGuard.ReparsePointCheckMetrics metrics))
            throw new IOException("The signature cache directory is not physical.");
        segmentsChecked = metrics.SegmentsChecked;
        return root;
    }

    private static long ElapsedMilliseconds(long started, long completed) =>
        (long)Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;

    [LoggerMessage(100, LogLevel.Debug,
        "EPUB signature cache read completed. Hit={CacheHit}, Bytes={EntryBytes}, RootSegmentsChecked={RootSegmentsChecked}, RootValidationMilliseconds={RootValidationMilliseconds}, ReadMilliseconds={ReadMilliseconds}, DeserializeMilliseconds={DeserializeMilliseconds}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogCacheRead(
        ILogger logger,
        bool cacheHit,
        long entryBytes,
        int rootSegmentsChecked,
        long rootValidationMilliseconds,
        long readMilliseconds,
        long deserializeMilliseconds,
        long totalMilliseconds);

    [LoggerMessage(101, LogLevel.Debug,
        "EPUB signature cache write completed. Bytes={EntryBytes}, RootSegmentsChecked={RootSegmentsChecked}, RootValidationMilliseconds={RootValidationMilliseconds}, SerializeMilliseconds={SerializeMilliseconds}, WriteMilliseconds={WriteMilliseconds}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogCacheWrite(
        ILogger logger,
        long entryBytes,
        int rootSegmentsChecked,
        long rootValidationMilliseconds,
        long serializeMilliseconds,
        long writeMilliseconds,
        long totalMilliseconds);

    [LoggerMessage(102, LogLevel.Information,
        "EPUB signature cache prune completed. EntriesScanned={EntriesScanned}, EntriesRemoved={EntriesRemoved}, BytesBefore={BytesBefore}, BytesAfter={BytesAfter}, RootSegmentsChecked={RootSegmentsChecked}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogCachePrune(
        ILogger logger,
        int entriesScanned,
        int entriesRemoved,
        long bytesBefore,
        long bytesAfter,
        int rootSegmentsChecked,
        long totalMilliseconds);

    private static CacheDocument CreateDocument(
        EpubContentSignatureCacheKey key,
        EpubContentSignature signature) => new(
        SchemaVersion,
        key.Value,
        signature.Fingerprint.SizeInBytes,
        signature.Fingerprint.Sha256.Value,
        signature.PolicyVersion.Value,
        signature.TotalTokenCount,
        signature.SpineItemCount,
        signature.SampledChapterCount,
        signature.Landmarks.Select(value => new LandmarkDocument(
            value.Ordinal,
            value.StartToken,
            value.TokenCount,
            value.DistinctTokenCount,
            value.StrictHash.Value,
            value.RelaxedHash.Value)).ToArray(),
        signature.DetectedLanguage,
        signature.LanguageConfidencePermille,
        signature.AnalysisTruncated,
        signature.ShingleMinHashes.ToArray());

    private static EpubContentSignature Rehydrate(
        CacheDocument document,
        EpubContentSignatureCacheKey key)
    {
        if (!string.Equals(document.SchemaVersion, SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(document.Key, key.Value, StringComparison.Ordinal)
            || !string.Equals(document.PolicyVersion, ContentSignaturePolicyVersion.Current.Value, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.FingerprintSha256)
            || document.Landmarks is null)
            throw new InvalidDataException("The signature cache entry version or key is invalid.");
        FormatFileFingerprint fingerprint = new(document.FingerprintSize, new(document.FingerprintSha256));
        if (fingerprint != key.Fingerprint)
            throw new InvalidDataException("The signature cache fingerprint is invalid.");
        ContentLandmarkSignature[] landmarks = document.Landmarks.Select(value => new ContentLandmarkSignature(
            value.Ordinal,
            value.StartToken,
            value.TokenCount,
            value.DistinctTokenCount,
            new(value.StrictHash),
            new(value.RelaxedHash))).ToArray();
        return new(
            fingerprint,
            document.TotalTokenCount,
            document.SpineItemCount,
            document.SampledChapterCount,
            landmarks,
            new(document.PolicyVersion),
            document.DetectedLanguage,
            document.LanguageConfidencePermille,
            document.AnalysisTruncated,
            document.ShingleMinHashes ?? []);
    }

    private sealed record CacheDocument(
        string SchemaVersion,
        string Key,
        long FingerprintSize,
        string FingerprintSha256,
        string PolicyVersion,
        long TotalTokenCount,
        int SpineItemCount,
        int SampledChapterCount,
        LandmarkDocument[] Landmarks,
        string? DetectedLanguage,
        int? LanguageConfidencePermille,
        bool AnalysisTruncated,
        ulong[]? ShingleMinHashes);

    private sealed record LandmarkDocument(
        int Ordinal,
        long StartToken,
        int TokenCount,
        int DistinctTokenCount,
        string StrictHash,
        string RelaxedHash);
}
