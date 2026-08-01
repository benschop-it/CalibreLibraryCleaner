using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

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
            LibrarySnapshot? snapshot = await TryReadFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            string canonicalRoot = CanonicalizeLibraryRoot(snapshot.Identity.LibraryRoot);
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

    private async Task<LibrarySnapshot?> TryReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

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
