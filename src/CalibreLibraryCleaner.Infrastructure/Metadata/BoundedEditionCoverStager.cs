using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
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

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumCoverBytes { get; init; } = 512 * 1024;
    public long MaximumBatchBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaximumCoverCount { get; init; } = 50_000;
    public int MaximumDimensionPixels { get; init; } = 10_000;
}

internal sealed class BoundedEditionCoverStager(
    HttpClient httpClient,
    EditionCoverStagingOptions options) : IEditionCoverStager, IDisposable
{
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
            return Failed("METADATA_COVER.REQUEST_INVALID");
        string? batchRoot = null;
        try
        {
            string storageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
            Directory.CreateDirectory(storageRoot);
            if (!ExecutionPathGuard.TryRejectReparsePoints(storageRoot, true, out _))
                return Failed("METADATA_COVER.STAGING_UNSAFE");
            batchRoot = Path.Combine(storageRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(batchRoot);
            if (!ExecutionPathGuard.TryRejectReparsePoints(batchRoot, true, out _))
                return Failed("METADATA_COVER.STAGING_UNSAFE");

            Dictionary<EditionCoverReference, StagedEditionCover> stagedByReference = [];
            Dictionary<string, StagedEditionCover> stagedByKey = new(StringComparer.Ordinal);
            long totalBytes = 0;
            int completed = 0;
            foreach (EditionCoverStagingRequest request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!stagedByReference.TryGetValue(request.Cover, out StagedEditionCover? staged))
                {
                    staged = await DownloadAsync(
                        request.Cover, batchRoot, stagedByReference.Count, totalBytes, cancellationToken)
                        .ConfigureAwait(false);
                    totalBytes = checked(totalBytes + staged.Fingerprint.SizeInBytes);
                    stagedByReference.Add(request.Cover, staged);
                }
                stagedByKey.Add(request.Key, staged);
                progress?.Report(new(++completed, requests.Count));
            }
            return new(new Session(batchRoot, stagedByKey), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(batchRoot);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                           or UnauthorizedAccessException or InvalidDataException
                                           or ArgumentException or OverflowException
                                           or CryptographicException or OperationCanceledException)
        {
            Delete(batchRoot);
            return Failed("METADATA_COVER.STAGING_FAILED");
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
        CancellationToken cancellationToken)
    {
        Uri uri = new(cover.Url, UriKind.Absolute);
        if (!IsTrustedOpenLibraryCover(uri))
            throw new InvalidDataException("The cover endpoint is not supported.");
        string fileName = $"cover-{index:D5}.jpg";
        string finalPath = Path.Combine(batchRoot, fileName);
        string temporaryPath = finalPath + ".tmp";
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentType?.MediaType is not "image/jpeg"
                || response.Content.Headers.ContentLength > options.MaximumCoverBytes)
                throw new InvalidDataException("The cover response is invalid.");
            await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[32 * 1024];
                int read;
                long written = 0;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    written = checked(written + read);
                    if (written > options.MaximumCoverBytes
                        || existingBytes + written > options.MaximumBatchBytes)
                        throw new InvalidDataException("The staged covers exceed their configured bound.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            ValidateJpegDimensions(temporaryPath);
            byte[] digest;
            long size;
            await using (FileStream staged = new(temporaryPath, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                size = staged.Length;
                digest = await SHA256.HashDataAsync(staged, cancellationToken).ConfigureAwait(false);
            }
            if (size == 0) throw new InvalidDataException("The cover response is empty.");
            File.Move(temporaryPath, finalPath, overwrite: false);
            return new(fileName, new(size, new(Convert.ToHexString(digest).ToLowerInvariant())));
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
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

    private static bool IsTrustedOpenLibraryCover(Uri uri)
    {
        const string prefix = "/b/id/";
        const string suffix = "-L.jpg";
        string id = uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)
            ? uri.AbsolutePath[prefix.Length..^suffix.Length]
            : string.Empty;
        return uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("covers.openlibrary.org", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && long.TryParse(id, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long coverId)
        && coverId > 0;
    }

    private static EditionCoverStagingResult Failed(string code) => new(null, code);

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
}
