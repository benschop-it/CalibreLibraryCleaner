using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;
using Newtonsoft.Json;

namespace CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;

internal sealed class VersionedJsonLibrarySnapshotStore(LibrarySnapshotStorageOptions options) : ILibrarySnapshotStore
{
    private const string FileSuffix = ".library-snapshot.json";

    public async Task<IReadOnlyList<PersistedLibrarySnapshotInfo>> ListAsync(CancellationToken cancellationToken)
    {
        string storageRoot = GetStorageRoot(mustExist: false);
        if (!Directory.Exists(storageRoot))
        {
            return [];
        }

        List<PersistedLibrarySnapshotInfo> snapshots = [];
        foreach (string path in Directory.EnumerateFiles(storageRoot, $"*{FileSuffix}", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistedLibrarySnapshotInfo? snapshot = await TryReadInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            string canonicalRoot = CanonicalizeLibraryRoot(snapshot.LibraryRoot);
            if (!string.Equals(Path.GetFileName(path), GetFileName(canonicalRoot), StringComparison.Ordinal))
            {
                continue;
            }

            snapshots.Add(new(canonicalRoot, snapshot.ScannedAt));
        }

        return snapshots
            .GroupBy(snapshot => snapshot.LibraryRoot, PathComparer)
            .Select(group => group.OrderByDescending(snapshot => snapshot.ScannedAt).First())
            .OrderBy(snapshot => snapshot.LibraryRoot, PathComparer)
            .ToArray();
    }

    public async Task<LibrarySnapshot?> ReadAsync(string libraryRoot, CancellationToken cancellationToken)
    {
        string canonicalRoot = CanonicalizeLibraryRoot(libraryRoot);
        string storageRoot = GetStorageRoot(mustExist: false);
        if (!Directory.Exists(storageRoot))
        {
            return null;
        }

        string path = Path.Combine(storageRoot, GetFileName(canonicalRoot));
        if (!File.Exists(path))
        {
            return null;
        }

        LibrarySnapshot snapshot = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!PathComparer.Equals(CanonicalizeLibraryRoot(snapshot.Identity.LibraryRoot), canonicalRoot))
        {
            throw new InvalidDataException("The persisted snapshot library key does not match its content.");
        }

        return snapshot;
    }

    public async Task WriteAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        string libraryRoot = CanonicalizeLibraryRoot(snapshot.Identity.LibraryRoot);
        string storageRoot = GetStorageRoot(mustExist: false);
        if (IsSameOrContained(libraryRoot, storageRoot) || IsSameOrContained(storageRoot, libraryRoot))
        {
            throw new InvalidOperationException("Persisted snapshots must be stored outside the Calibre library.");
        }

        Directory.CreateDirectory(storageRoot);
        storageRoot = GetStorageRoot(mustExist: true);
        string path = Path.Combine(storageRoot, GetFileName(libraryRoot));
        if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
        {
            throw new IOException("The persisted snapshot is not a physical file.");
        }

        byte[] bytes = LibrarySnapshotJsonSerializer.Serialize(snapshot);
        if (bytes.LongLength > options.MaximumSnapshotBytes)
        {
            throw new InvalidDataException("The persisted snapshot exceeds its configured size bound.");
        }

        string temporary = Path.Combine(storageRoot, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public Task DeleteAsync(string libraryRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonicalRoot = CanonicalizeLibraryRoot(libraryRoot);
        string storageRoot = GetStorageRoot(mustExist: false);
        if (!Directory.Exists(storageRoot)) return Task.CompletedTask;
        string path = Path.Combine(storageRoot, GetFileName(canonicalRoot));
        if (!File.Exists(path)) return Task.CompletedTask;
        if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
            throw new IOException("The persisted snapshot is not a physical file.");
        File.Delete(path);
        return Task.CompletedTask;
    }

    private async Task<PersistedLibrarySnapshotInfo?> TryReadInfoAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadInfoAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or ArgumentException or NotSupportedException
                                           or OverflowException)
        {
            return null;
        }
    }

    private async Task<PersistedLibrarySnapshotInfo> ReadInfoAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
        {
            throw new IOException("The persisted snapshot is not a physical file.");
        }

        FileInfo info = new(path);
        if (info.Length <= 0 || info.Length > options.MaximumSnapshotBytes)
        {
            throw new InvalidDataException("The persisted snapshot size is invalid.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader textReader = new(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4 * 1024,
            leaveOpen: true);
        using JsonTextReader jsonReader = new(textReader)
        {
            DateParseHandling = DateParseHandling.DateTimeOffset,
            MaxDepth = 64,
        };

        try
        {
            await RequireTokenAsync(jsonReader, JsonToken.StartObject, cancellationToken).ConfigureAwait(false);
            string? schemaVersion = null;
            PersistedLibrarySnapshotInfo? snapshot = null;
            while (await jsonReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (jsonReader.TokenType == JsonToken.EndObject)
                {
                    break;
                }

                string propertyName = RequirePropertyName(jsonReader);
                await RequireValueAsync(jsonReader, cancellationToken).ConfigureAwait(false);
                switch (propertyName)
                {
                    case "schemaVersion" when schemaVersion is null:
                        schemaVersion = jsonReader.TokenType == JsonToken.String
                            ? (string?)jsonReader.Value
                            : throw new InvalidDataException("The persisted snapshot version is invalid.");
                        break;
                    case "snapshot" when snapshot is null:
                        snapshot = await ReadSnapshotInfoAsync(jsonReader, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException("The persisted snapshot metadata is malformed.");
                }

                if (snapshot is not null && schemaVersion is not null)
                {
                    if (!string.Equals(schemaVersion, LibrarySnapshotJsonSerializer.SchemaVersion, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The persisted library snapshot version is not supported.");
                    }

                    return snapshot;
                }
            }

            throw new InvalidDataException("The persisted snapshot metadata is incomplete.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The persisted snapshot metadata is malformed.", exception);
        }
    }

    private static async Task<PersistedLibrarySnapshotInfo> ReadSnapshotInfoAsync(
        JsonTextReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.TokenType != JsonToken.StartObject)
        {
            throw new InvalidDataException("The persisted snapshot body is invalid.");
        }

        string? libraryRoot = null;
        DateTimeOffset? scannedAt = null;
        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.TokenType == JsonToken.EndObject)
            {
                break;
            }

            string propertyName = RequirePropertyName(reader);
            if (!propertyNames.Add(propertyName))
            {
                throw new InvalidDataException("The persisted snapshot metadata contains a duplicate property.");
            }
            await RequireValueAsync(reader, cancellationToken).ConfigureAwait(false);
            switch (propertyName)
            {
                case "identity" when libraryRoot is null:
                    libraryRoot = await ReadLibraryRootAsync(reader, cancellationToken).ConfigureAwait(false);
                    break;
                case "scannedAt" when scannedAt is null:
                    scannedAt = ReadDateTimeOffset(reader);
                    break;
                default:
                    throw new InvalidDataException("The persisted snapshot listing metadata is incomplete.");
            }

            if (libraryRoot is not null && scannedAt is not null)
            {
                return new(libraryRoot, scannedAt.Value);
            }
        }

        throw new InvalidDataException("The persisted snapshot listing metadata is incomplete.");
    }

    private static async Task<string> ReadLibraryRootAsync(
        JsonTextReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.TokenType != JsonToken.StartObject)
        {
            throw new InvalidDataException("The persisted snapshot identity is invalid.");
        }

        string? libraryRoot = null;
        bool hasCalibreLibraryUuid = false;
        bool hasSchemaVersion = false;
        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.TokenType == JsonToken.EndObject)
            {
                break;
            }

            string propertyName = RequirePropertyName(reader);
            if (!propertyNames.Add(propertyName))
            {
                throw new InvalidDataException("The persisted snapshot identity contains a duplicate property.");
            }
            await RequireValueAsync(reader, cancellationToken).ConfigureAwait(false);
            switch (propertyName)
            {
                case "calibreLibraryUuid" when reader.TokenType == JsonToken.String
                                                 && reader.Value is string uuid
                                                 && !string.IsNullOrWhiteSpace(uuid):
                    hasCalibreLibraryUuid = true;
                    break;
                case "schemaVersion" when reader.TokenType == JsonToken.Integer
                                           && Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture) > 0:
                    hasSchemaVersion = true;
                    break;
                case "libraryRoot" when reader.TokenType == JsonToken.String:
                    libraryRoot = (string?)reader.Value;
                    break;
                default:
                    throw new InvalidDataException("The persisted snapshot identity is invalid.");
            }
        }

        return !hasCalibreLibraryUuid || !hasSchemaVersion || string.IsNullOrWhiteSpace(libraryRoot)
            ? throw new InvalidDataException("The persisted snapshot library root is missing.")
            : libraryRoot;
    }

    private static DateTimeOffset ReadDateTimeOffset(JsonTextReader reader) => reader.Value switch
    {
        DateTimeOffset value => value,
        DateTime value => new(value),
        string value when DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed) => parsed,
        _ => throw new InvalidDataException("The persisted snapshot scan time is invalid."),
    };

    private static async Task RequireTokenAsync(
        JsonTextReader reader,
        JsonToken expected,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.TokenType != expected)
        {
            throw new InvalidDataException("The persisted snapshot metadata is malformed.");
        }
    }

    private static async Task RequireValueAsync(JsonTextReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The persisted snapshot metadata is incomplete.");
        }
    }

    private static string RequirePropertyName(JsonTextReader reader) =>
        reader.TokenType == JsonToken.PropertyName && reader.Value is string propertyName
            ? propertyName
            : throw new InvalidDataException("The persisted snapshot metadata is malformed.");

    private async Task<LibrarySnapshot> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
        {
            throw new IOException("The persisted snapshot is not a physical file.");
        }

        FileInfo info = new(path);
        if (info.Length <= 0 || info.Length > options.MaximumSnapshotBytes || info.Length > int.MaxValue)
        {
            throw new InvalidDataException("The persisted snapshot size is invalid.");
        }

        byte[] bytes = new byte[(int)info.Length];
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        LibrarySnapshotJsonReadResult result = LibrarySnapshotJsonSerializer.Deserialize(bytes);
        return result.Snapshot ?? throw new InvalidDataException(result.Error);
    }

    private string GetStorageRoot(bool mustExist)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        if (mustExist && !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The persisted snapshot directory does not exist.");
        }

        if (!ExecutionPathGuard.TryRejectReparsePoints(root, mustExist, out _))
        {
            throw new IOException("The persisted snapshot directory is not a physical directory.");
        }

        return root;
    }

    private static string CanonicalizeLibraryRoot(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
    }

    private static string GetFileName(string canonicalLibraryRoot)
    {
        string key = OperatingSystem.IsWindows() ? canonicalLibraryRoot.ToUpperInvariant() : canonicalLibraryRoot;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return digest + FileSuffix;
    }

    private static bool IsSameOrContained(string root, string candidate)
    {
        if (PathComparer.Equals(root, candidate))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
