using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Metadata;

public sealed record EditionCoverStagingOptions
{
    public string StorageRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "metadata-cover-staging");

    public string CacheRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "metadata-cover-cache");

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public int MaximumCoverBytes { get; init; } = 512 * 1024;
    public long MaximumBatchBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaximumCoverCount { get; init; } = 50_000;
    public int MaximumDimensionPixels { get; init; } = 10_000;
    public long MaximumCacheBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaximumCacheEntries { get; init; } = 50_000;
}

internal sealed class BoundedEditionCoverStager(
    HttpClient httpClient,
    EditionCoverStagingOptions options) : IEditionCoverStager, IDisposable
{
    private const string CacheSchemaVersion = "metadata-cover-cache/1.0";
    private const int MaximumRedirects = 2;
    private const int MaximumAttempts = 3;
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4,
    };
    private int _disposed;

    public async Task<EditionCoverStagingResult> StageAsync(
        IReadOnlyList<EditionCoverStagingRequest> requests,
        IProgress<EditionCoverStagingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count > options.MaximumCoverCount
            || requests.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            return Failed("METADATA_COVER.REQUEST_INVALID", 0, requests.Count);
        string? batchRoot = null;
        int completed = 0;
        int cacheHits = 0;
        int downloads = 0;
        int omitted = 0;
        try
        {
            string storageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
            Directory.CreateDirectory(storageRoot);
            if (!ExecutionPathGuard.TryRejectReparsePoints(storageRoot, true, out _))
                return Failed("METADATA_COVER.STAGING_UNSAFE", completed, requests.Count);
            batchRoot = Path.Combine(storageRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(batchRoot);
            if (!ExecutionPathGuard.TryRejectReparsePoints(batchRoot, true, out _))
                return Failed("METADATA_COVER.STAGING_UNSAFE", completed, requests.Count);
            (string? cacheRoot, CacheBudget? cacheBudget) = TryPrepareCache();

            Dictionary<EditionCoverReference, StagedEditionCover> stagedByReference = [];
            Dictionary<EditionCoverReference, string> skippedByReference = [];
            Dictionary<string, StagedEditionCover> stagedByKey = new(StringComparer.Ordinal);
            Dictionary<string, string> skippedByKey = new(StringComparer.Ordinal);
            long totalBytes = 0;
            foreach (EditionCoverStagingRequest request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (skippedByReference.TryGetValue(request.Cover, out string? skipCode))
                {
                    skippedByKey.Add(request.Key, skipCode);
                }
                else if (!stagedByReference.TryGetValue(request.Cover, out StagedEditionCover? staged))
                {
                    try
                    {
                        staged = await TryMaterializeCachedAsync(
                            request.Cover,
                            batchRoot,
                            stagedByReference.Count,
                            totalBytes,
                            cacheRoot,
                            cacheBudget,
                            cancellationToken).ConfigureAwait(false);
                        if (staged is not null)
                        {
                            cacheHits++;
                        }
                        else
                        {
                            staged = await DownloadAsync(
                                request.Cover,
                                batchRoot,
                                stagedByReference.Count,
                                totalBytes,
                                cacheRoot,
                                cacheBudget,
                                cancellationToken).ConfigureAwait(false);
                            downloads++;
                        }
                        totalBytes = checked(totalBytes + staged.Fingerprint.SizeInBytes);
                        stagedByReference.Add(request.Cover, staged);
                        stagedByKey.Add(request.Key, staged);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        skippedByReference.Add(request.Cover, "METADATA_COVER.TIMEOUT");
                        skippedByKey.Add(request.Key, "METADATA_COVER.TIMEOUT");
                        omitted++;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TransientCoverException)
                    {
                        skippedByReference.Add(request.Cover, "METADATA_COVER.HTTP_FAILED");
                        skippedByKey.Add(request.Key, "METADATA_COVER.HTTP_FAILED");
                        omitted++;
                    }
                }
                else
                {
                    stagedByKey.Add(request.Key, staged);
                }
                completed++;
                progress?.Report(new(completed, requests.Count, cacheHits, downloads, omitted));
            }
            return new(
                new Session(batchRoot, stagedByKey),
                null,
                completed,
                requests.Count,
                new ReadOnlyDictionary<string, string>(skippedByKey));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(batchRoot);
            throw;
        }
        catch (OperationCanceledException)
        {
            Delete(batchRoot);
            return Failed("METADATA_COVER.TIMEOUT", completed, requests.Count);
        }
        catch (Exception exception) when (exception is HttpRequestException or TransientCoverException)
        {
            Delete(batchRoot);
            return Failed("METADATA_COVER.HTTP_FAILED", completed, requests.Count);
        }
        catch (InvalidDataException)
        {
            Delete(batchRoot);
            return Failed("METADATA_COVER.RESPONSE_INVALID", completed, requests.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or ArgumentException or OverflowException or CryptographicException)
        {
            Delete(batchRoot);
            return Failed("METADATA_COVER.STAGING_FAILED", completed, requests.Count);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) httpClient.Dispose();
    }

    private async Task<StagedEditionCover> DownloadAsync(
        EditionCoverReference cover,
        string batchRoot,
        int index,
        long existingBytes,
        string? cacheRoot,
        CacheBudget? cacheBudget,
        CancellationToken cancellationToken)
    {
        Uri uri = new(cover.Url, UriKind.Absolute);
        if (!TryGetTrustedOpenLibraryCoverId(uri, out string? coverId)
            || cover.SourceId != coverId)
            throw new InvalidDataException("The cover endpoint is not supported.");
        string fileName = $"cover-{index:D5}.jpg";
        string finalPath = Path.Combine(batchRoot, fileName);
        string temporaryPath = finalPath + ".tmp";
        try
        {
            for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    timeout.CancelAfter(options.RequestTimeout);
                    using HttpResponseMessage response = await SendAsync(
                        uri, coverId, timeout.Token).ConfigureAwait(false);
                    if (IsTransient(response.StatusCode))
                        throw new TransientCoverException();
                    if (response.StatusCode != HttpStatusCode.OK
                        || response.Content.Headers.ContentType?.MediaType is not "image/jpeg"
                        || response.Content.Headers.ContentLength > options.MaximumCoverBytes)
                        throw new InvalidDataException("The cover response is invalid.");
                    await WriteResponseAsync(
                        response, temporaryPath, existingBytes, timeout.Token).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                         && attempt < MaximumAttempts)
                {
                    File.Delete(temporaryPath);
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < MaximumAttempts)
                {
                    File.Delete(temporaryPath);
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (TransientCoverException) when (attempt < MaximumAttempts)
                {
                    File.Delete(temporaryPath);
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
            }
            ValidateJpegDimensions(temporaryPath);
            byte[] digest;
            long size;
            await using (FileStream downloaded = new(temporaryPath, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                size = downloaded.Length;
                digest = await SHA256.HashDataAsync(downloaded, cancellationToken).ConfigureAwait(false);
            }
            if (size == 0) throw new InvalidDataException("The cover response is empty.");
            File.Move(temporaryPath, finalPath, overwrite: false);
            StagedEditionCover staged = new(
                fileName,
                new(size, new(Convert.ToHexString(digest).ToLowerInvariant())));
            await TryWriteCacheAsync(
                cover, finalPath, staged.Fingerprint, cacheRoot, cacheBudget, cancellationToken)
                .ConfigureAwait(false);
            return staged;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<StagedEditionCover?> TryMaterializeCachedAsync(
        EditionCoverReference cover,
        string batchRoot,
        int index,
        long existingBytes,
        string? cacheRoot,
        CacheBudget? budget,
        CancellationToken cancellationToken)
    {
        ValidateCoverReference(cover);
        if (cacheRoot is null || budget is null) return null;
        string identity = CreateCacheIdentity(cover);
        string dataPath = Path.Combine(cacheRoot, identity + ".jpg");
        string manifestPath = Path.Combine(cacheRoot, identity + ".json");
        try
        {
            if (!File.Exists(dataPath) || !File.Exists(manifestPath)
                || !ExecutionPathGuard.TryRejectReparsePointLeaf(dataPath, true, out _)
                || !ExecutionPathGuard.TryRejectReparsePointLeaf(manifestPath, true, out _))
            {
                RemoveCacheEntry(identity, budget);
                return null;
            }
            FileInfo manifestInfo = new(manifestPath);
            if (manifestInfo.Length is <= 0 or > 4096) throw new InvalidDataException();
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            CacheDocument document = JsonSerializer.Deserialize<CacheDocument>(manifestBytes, CacheJsonOptions)
                ?? throw new InvalidDataException();
            FileInfo dataInfo = new(dataPath);
            if (document.SchemaVersion != CacheSchemaVersion
                || document.Identity != identity
                || document.SizeInBytes != dataInfo.Length
                || document.SizeInBytes is <= 0
                || document.SizeInBytes > options.MaximumCoverBytes
                || existingBytes + document.SizeInBytes > options.MaximumBatchBytes)
                throw new InvalidDataException();
            ValidateJpegDimensions(dataPath);
            byte[] digest;
            await using (FileStream cached = new(
                             dataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
                digest = await SHA256.HashDataAsync(cached, cancellationToken).ConfigureAwait(false);
            string sha256 = Convert.ToHexString(digest).ToLowerInvariant();
            if (!string.Equals(document.Sha256, sha256, StringComparison.Ordinal))
                throw new InvalidDataException();
            string fileName = $"cover-{index:D5}.jpg";
            File.Copy(dataPath, Path.Combine(batchRoot, fileName), overwrite: false);
            DateTime now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(dataPath, now);
            File.SetLastWriteTimeUtc(manifestPath, now);
            return new(fileName, new(document.SizeInBytes, new(sha256)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or InvalidDataException or ArgumentException
                                           or OverflowException or CryptographicException)
        {
            RemoveCacheEntry(identity, budget);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(
        EditionCoverReference cover,
        string sourcePath,
        FormatFileFingerprint fingerprint,
        string? cacheRoot,
        CacheBudget? budget,
        CancellationToken cancellationToken)
    {
        if (cacheRoot is null || budget is null) return;
        string identity = CreateCacheIdentity(cover);
        CacheDocument document = new(
            CacheSchemaVersion,
            identity,
            fingerprint.SizeInBytes,
            fingerprint.Sha256.Value);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(document, CacheJsonOptions);
        long incomingBytes = checked(fingerprint.SizeInBytes + manifestBytes.LongLength);
        if (incomingBytes > options.MaximumCacheBytes || options.MaximumCacheEntries <= 0) return;
        string dataPath = Path.Combine(cacheRoot, identity + ".jpg");
        string manifestPath = Path.Combine(cacheRoot, identity + ".json");
        string temporaryData = Path.Combine(cacheRoot, $".{Guid.NewGuid():N}.jpg.tmp");
        string temporaryManifest = Path.Combine(cacheRoot, $".{Guid.NewGuid():N}.json.tmp");
        try
        {
            RemoveCacheEntry(identity, budget);
            TrimCache(budget, incomingBytes, 1);
            if (budget.Bytes + incomingBytes > options.MaximumCacheBytes
                || budget.Entries.Count + 1 > options.MaximumCacheEntries) return;
            File.Copy(sourcePath, temporaryData, overwrite: false);
            await File.WriteAllBytesAsync(temporaryManifest, manifestBytes, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryData, dataPath, overwrite: true);
            File.Move(temporaryManifest, manifestPath, overwrite: true);
            CacheEntry entry = new(identity, dataPath, manifestPath, incomingBytes);
            budget.Entries.Add(identity, entry);
            budget.Oldest.Enqueue(entry);
            budget.Bytes = checked(budget.Bytes + incomingBytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or InvalidDataException or ArgumentException
                                           or OverflowException or CryptographicException)
        { }
        finally
        {
            TryDeleteFile(temporaryData);
            TryDeleteFile(temporaryManifest);
        }
    }

    private (string? Root, CacheBudget? Budget) TryPrepareCache()
    {
        try
        {
            string root = GetCacheRoot();
            return (root, PrepareCache(root));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or InvalidDataException or ArgumentException
                                           or OverflowException or CryptographicException)
        {
            return (null, null);
        }
    }

    private string GetCacheRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.CacheRoot));
        Directory.CreateDirectory(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, true, out _))
            throw new IOException("The metadata cover cache directory is not physical.");
        return root;
    }

    private CacheBudget PrepareCache(string cacheRoot)
    {
        foreach (string temporary in Directory.EnumerateFiles(cacheRoot, ".*.tmp", SearchOption.TopDirectoryOnly))
            TryDeleteFile(temporary);
        Dictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
        foreach (string dataPath in Directory.EnumerateFiles(cacheRoot, "*.jpg", SearchOption.TopDirectoryOnly))
        {
            string identity = Path.GetFileNameWithoutExtension(dataPath);
            string manifestPath = Path.Combine(cacheRoot, identity + ".json");
            if (!File.Exists(manifestPath)
                || !ExecutionPathGuard.TryRejectReparsePointLeaf(dataPath, true, out _)
                || !ExecutionPathGuard.TryRejectReparsePointLeaf(manifestPath, true, out _))
            {
                TryDeleteFile(dataPath);
                TryDeleteFile(manifestPath);
                continue;
            }
            long bytes = checked(new FileInfo(dataPath).Length + new FileInfo(manifestPath).Length);
            entries[identity] = new(identity, dataPath, manifestPath, bytes);
        }
        foreach (string manifestPath in Directory.EnumerateFiles(cacheRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            string identity = Path.GetFileNameWithoutExtension(manifestPath);
            if (!entries.ContainsKey(identity)) TryDeleteFile(manifestPath);
        }
        Queue<CacheEntry> oldest = new(entries.Values
            .OrderBy(value => File.GetLastWriteTimeUtc(value.DataPath))
            .ThenBy(value => value.Identity, StringComparer.Ordinal));
        CacheBudget budget = new(entries, oldest, entries.Values.Sum(value => value.Bytes));
        TrimCache(budget, 0, 0);
        return budget;
    }

    private void TrimCache(CacheBudget budget, long incomingBytes, int incomingEntries)
    {
        while ((budget.Bytes + incomingBytes > options.MaximumCacheBytes
                || budget.Entries.Count + incomingEntries > options.MaximumCacheEntries)
               && budget.Oldest.TryDequeue(out CacheEntry? oldest))
        {
            if (!budget.Entries.Remove(oldest.Identity)) continue;
            TryDeleteFile(oldest.DataPath);
            TryDeleteFile(oldest.ManifestPath);
            budget.Bytes -= oldest.Bytes;
        }
    }

    private static void RemoveCacheEntry(string identity, CacheBudget budget)
    {
        if (!budget.Entries.Remove(identity, out CacheEntry? entry)) return;
        TryDeleteFile(entry.DataPath);
        TryDeleteFile(entry.ManifestPath);
        budget.Bytes -= entry.Bytes;
    }

    private string CreateCacheIdentity(EditionCoverReference cover)
    {
        StringBuilder canonical = new();
        Append(canonical, CacheSchemaVersion);
        Append(canonical, cover.SourceId);
        Append(canonical, cover.Url);
        Append(canonical, options.MaximumCoverBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, options.MaximumDimensionPixels.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void ValidateCoverReference(EditionCoverReference cover)
    {
        Uri uri = new(cover.Url, UriKind.Absolute);
        if (!TryGetTrustedOpenLibraryCoverId(uri, out string? coverId)
            || cover.SourceId != coverId)
            throw new InvalidDataException("The cover endpoint is not supported.");
    }

    private static void Append(StringBuilder target, string value) => target
        .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
        .Append(':').Append(value).Append('|');

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        options.RetryDelay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(options.RetryDelay * attempt, cancellationToken);

    private async Task WriteResponseAsync(
        HttpResponseMessage response,
        string temporaryPath,
        long existingBytes,
        CancellationToken cancellationToken)
    {
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[32 * 1024];
        int read;
        long written = 0;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            written = checked(written + read);
            if (written > options.MaximumCoverBytes
                || existingBytes + written > options.MaximumBatchBytes)
                throw new InvalidDataException("The staged covers exceed their configured bound.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri initialUri,
        string coverId,
        CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        string? archiveMemberFile = null;
        for (int redirects = 0; ; redirects++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return response;
            if (response.Headers.Location is null)
            {
                response.Dispose();
                throw new InvalidDataException("The cover redirect chain is invalid.");
            }
            if (redirects >= MaximumRedirects)
            {
                response.Dispose();
                throw new TransientCoverException();
            }

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new(current, response.Headers.Location);
            response.Dispose();
            bool trusted = redirects switch
            {
                0 => TryGetTrustedArchiveCoverMember(next, out archiveMemberFile),
                1 => archiveMemberFile is not null && IsTrustedArchiveCdnCover(next, archiveMemberFile),
                _ => false,
            };
            if (!trusted) throw new InvalidDataException("The cover redirect target is not supported.");
            current = next;
        }
    }

    private void ValidateJpegDimensions(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
            throw new InvalidDataException("The cover is not a JPEG image.");
        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> dimensions = stackalloc byte[5];
        while (stream.Position < stream.Length)
        {
            int prefix;
            do { prefix = stream.ReadByte(); } while (prefix != -1 && prefix != 0xff);
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker == -1 || marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            stream.ReadExactly(lengthBytes);
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
                throw new InvalidDataException("The JPEG segment is invalid.");
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7
                or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                stream.ReadExactly(dimensions);
                int height = BinaryPrimitives.ReadUInt16BigEndian(dimensions[1..3]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(dimensions[3..5]);
                if (width is < 1 || height is < 1
                    || width > options.MaximumDimensionPixels || height > options.MaximumDimensionPixels)
                    throw new InvalidDataException("The JPEG dimensions exceed their configured bound.");
                return;
            }
            stream.Position += segmentLength - 2;
        }
        throw new InvalidDataException("The JPEG dimensions are unavailable.");
    }

    private static bool TryGetTrustedOpenLibraryCoverId(Uri uri, out string? coverId)
    {
        const string prefix = "/b/id/";
        const string suffix = "-L.jpg";
        string id = uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)
            ? uri.AbsolutePath[prefix.Length..^suffix.Length]
            : string.Empty;
        bool trusted = uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("covers.openlibrary.org", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && long.TryParse(id, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long parsedCoverId)
        && parsedCoverId > 0;
        coverId = trusted ? id : null;
        return trusted;
    }

    private static bool TryGetTrustedArchiveCoverMember(Uri uri, out string? memberFile)
    {
        memberFile = null;
        if (!IsPlainHttps(uri, "archive.org") || !string.IsNullOrEmpty(uri.Query)) return false;
        Match match = Regex.Match(uri.AbsolutePath,
            "^/download/l_covers_[0-9]+/l_covers_[0-9]+_[0-9]+\\.zip/(?<id>[0-9]+)(?<suffix>-L\\.jpg)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(uri.AbsolutePath,
                "^/download/olcovers[0-9]+/olcovers[0-9]+-L\\.zip/(?<id>[0-9]+)(?<suffix>-L\\.jpg)$",
                RegexOptions.CultureInvariant);
        if (!match.Success
            || !long.TryParse(match.Groups["id"].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long memberId)
            || memberId <= 0)
            return false;
        memberFile = match.Groups["id"].Value + match.Groups["suffix"].Value;
        return true;
    }

    private static bool IsTrustedArchiveCdnCover(Uri uri, string archiveMemberFile)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/view_archive.php"
            || !Regex.IsMatch(uri.Host, "^ia[0-9]+\\.us\\.archive\\.org$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        Dictionary<string, string> query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .ToDictionary(
                value => Uri.UnescapeDataString(value[0]),
                value => value.Length == 2 ? Uri.UnescapeDataString(value[1]) : string.Empty,
                StringComparer.Ordinal);
        return query.Count == 2
            && query.ContainsKey("archive")
            && query.TryGetValue("file", out string? file)
            && file == archiveMemberFile;
    }

    private static bool IsPlainHttps(Uri uri, string host) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static EditionCoverStagingResult Failed(string code, int completed, int total) =>
        new(null, code, completed, total);

    private static void Delete(string? path)
    {
        if (path is null) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Session : IEditionCoverStagingSession
    {
        private string? _root;

        public Session(string root, IReadOnlyDictionary<string, StagedEditionCover> covers)
        {
            _root = root;
            StagingRoot = root;
            Covers = new ReadOnlyDictionary<string, StagedEditionCover>(
                new Dictionary<string, StagedEditionCover>(covers, StringComparer.Ordinal));
        }

        public string StagingRoot { get; }
        public IReadOnlyDictionary<string, StagedEditionCover> Covers { get; }

        public ValueTask DisposeAsync()
        {
            Delete(Interlocked.Exchange(ref _root, null));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record CacheDocument(
        string SchemaVersion,
        string Identity,
        long SizeInBytes,
        string Sha256);

    private sealed record CacheEntry(
        string Identity,
        string DataPath,
        string ManifestPath,
        long Bytes);

    private sealed class CacheBudget(
        Dictionary<string, CacheEntry> entries,
        Queue<CacheEntry> oldest,
        long bytes)
    {
        public Dictionary<string, CacheEntry> Entries { get; } = entries;
        public Queue<CacheEntry> Oldest { get; } = oldest;
        public long Bytes { get; set; } = bytes;
    }

    private sealed class TransientCoverException : IOException;
}
