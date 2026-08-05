using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed class PersistedLibrarySnapshotsUseCase(
    ILibrarySnapshotStore store,
    ILibraryStateStore? stateStore = null)
{
    public async Task<PersistedLibrarySnapshotListResult> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<PersistedLibrarySnapshotInfo> snapshots = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PersistedLibraryStateInfo> states = stateStore is null
                ? []
                : await stateStore.ListAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, PersistedLibrarySnapshotInfo> byRoot = snapshots.ToDictionary(
                snapshot => snapshot.LibraryRoot,
                PathComparer);
            foreach (PersistedLibraryStateInfo state in states)
            {
                byRoot[state.LibraryRoot] = new(state.LibraryRoot, state.ScannedAt);
            }

            return new(byRoot.Values.OrderBy(snapshot => snapshot.LibraryRoot, PathComparer).ToArray(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new([], "Persisted scan results could not be listed.");
        }
    }

    public async Task<PersistedLibrarySnapshotLoadResult> LoadAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return new(null, "Select a library folder with a persisted scan result.");
        }

        try
        {
            LibrarySnapshot? snapshot = await store.ReadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? new(null, "No persisted scan result exists for the selected library folder.")
                : new(snapshot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(null, "The persisted scan result could not be loaded safely.");
        }
    }

    public async Task<PersistedLibrarySnapshotSaveResult> SaveAsync(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            await store.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return new(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(false, "The scan completed, but its result could not be persisted.");
        }
    }

    public async Task<PersistedLibrarySnapshotInvalidateResult> InvalidateAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            return new(false, "The persisted scan result could not be identified for invalidation.");
        try
        {
            await store.DeleteAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
            return new(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(false, "The persisted scan result could not be invalidated safely.");
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed record PersistedLibrarySnapshotListResult(
    IReadOnlyList<PersistedLibrarySnapshotInfo> Snapshots,
    string? Error);

public sealed record PersistedLibrarySnapshotLoadResult(LibrarySnapshot? Snapshot, string? Error)
{
    public bool IsSuccess => Snapshot is not null;
}

public sealed record PersistedLibrarySnapshotSaveResult(bool IsSuccess, string? Error);
public sealed record PersistedLibrarySnapshotInvalidateResult(bool IsSuccess, string? Error);
