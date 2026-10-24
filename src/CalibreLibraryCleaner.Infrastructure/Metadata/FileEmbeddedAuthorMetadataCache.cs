using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Metadata;

internal sealed class FileEmbeddedAuthorMetadataCache(EmbeddedAuthorMetadataCacheOptions options) :
    IEmbeddedAuthorMetadataCache,
    IFrontMatterAuthorCache
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

    public async Task<FrontMatterAuthorObservation?> TryReadAsync(
        FrontMatterAuthorCacheKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(StorageRoot(), FrontMatterFileName(key));
        if (!File.Exists(path)
            || !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return null;
        try
        {
            FileInfo info = new(path);
            if (info.Length is <= 0 || info.Length > options.MaximumEntryBytes) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            FrontMatterCacheDocument document = JsonSerializer.Deserialize<FrontMatterCacheDocument>(
                bytes, JsonOptions) ?? throw new InvalidDataException("The front-matter cache entry is empty.");
            if (document.SchemaVersion != "front-matter-identity-cache/2.0"
                || document.Candidates is null
                || document.IdentityCandidates is null
                || document.Format != key.Format
                || document.SizeInBytes != key.Fingerprint.SizeInBytes
                || document.Sha256 != key.Fingerprint.Sha256.Value
                || document.ExtractorVersion != key.ExtractorVersion
                || document.LimitsVersion != key.LimitsVersion)
                return null;
            return new(
                document.Format,
                key.Fingerprint,
                document.ExtractorVersion,
                document.LimitsVersion,
                document.Status,
                document.Candidates.Select(value => new FrontMatterAuthorCandidate(
                    value.Role, value.Names, value.UnitNumber, value.LineNumber, value.Marker)),
                document.RequestedUnits,
                document.CompletedUnits,
                document.RetainedUsefulCharacters,
                document.Truncated,
                document.ProblemCode,
                document.IdentityCandidates.Select(value => new FrontMatterIdentityCandidate(
                    value.Title, value.Authors, value.UnitNumber, value.TitleLineNumber,
                    value.AuthorLineNumber, value.ExplicitCredit, value.Subtitle)));
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
        FrontMatterAuthorObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        FrontMatterAuthorCacheKey key = new(
            observation.Format, observation.Fingerprint, observation.ExtractorVersion,
            observation.LimitsVersion);
        string root = StorageRoot();
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            root = StorageRoot();
        }
        string path = Path.Combine(root, FrontMatterFileName(key));
        if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _))
            throw new IOException("The front-matter author cache entry is not a physical file.");
        FrontMatterCacheDocument document = new(
            "front-matter-identity-cache/2.0",
            observation.Format,
            observation.Fingerprint.SizeInBytes,
            observation.Fingerprint.Sha256.Value,
            observation.ExtractorVersion,
            observation.LimitsVersion,
            observation.Status,
            observation.Candidates.Select(value => new FrontMatterCandidateDocument(
                value.Role, value.Names.ToArray(), value.UnitNumber, value.LineNumber, value.Marker)).ToArray(),
            observation.RequestedUnits,
            observation.CompletedUnits,
            observation.RetainedUsefulCharacters,
            observation.Truncated,
            observation.ProblemCode,
            observation.IdentityCandidates.Select(value => new IdentityCandidateDocument(
                value.Title, value.Authors.ToArray(), value.UnitNumber, value.TitleLineNumber,
                value.AuthorLineNumber, value.ExplicitCredit, value.Subtitle)).ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > options.MaximumEntryBytes)
            throw new InvalidDataException("The front-matter author cache entry exceeds its bound.");
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

    private static string FrontMatterFileName(FrontMatterAuthorCacheKey key)
    {
        string identity = string.Join('\u001F',
            "front-matter",
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

    private sealed record FrontMatterCandidateDocument(
        Domain.Metadata.AuthorCreditRole Role,
        string[] Names,
        int UnitNumber,
        int LineNumber,
        string Marker);

    private sealed record FrontMatterCacheDocument(
        string SchemaVersion,
        string Format,
        long SizeInBytes,
        string Sha256,
        string ExtractorVersion,
        string LimitsVersion,
        FrontMatterAuthorStatus Status,
        FrontMatterCandidateDocument[] Candidates,
        int RequestedUnits,
        int CompletedUnits,
        int RetainedUsefulCharacters,
        bool Truncated,
        string? ProblemCode,
        IdentityCandidateDocument[] IdentityCandidates);

    private sealed record IdentityCandidateDocument(
        string Title,
        string[] Authors,
        int UnitNumber,
        int TitleLineNumber,
        int AuthorLineNumber,
        bool ExplicitCredit,
        string? Subtitle);
}
