using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ILibrarySnapshotStore
{
    Task<IReadOnlyList<PersistedLibrarySnapshotInfo>> ListAsync(CancellationToken cancellationToken);

    Task<LibrarySnapshot?> ReadAsync(string libraryRoot, CancellationToken cancellationToken);

    Task WriteAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken);

    Task DeleteAsync(string libraryRoot, CancellationToken cancellationToken);
}

public sealed record PersistedLibrarySnapshotInfo(string LibraryRoot, DateTimeOffset ScannedAt);
