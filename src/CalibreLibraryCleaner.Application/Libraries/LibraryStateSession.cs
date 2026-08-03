using System.Collections.Concurrent;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryStateSessionOutcome(
    LibraryState? State,
    string? ErrorCode = null,
    string? Explanation = null)
{
    public bool IsSuccess => State is not null && ErrorCode is null;

    public static LibraryStateSessionOutcome Success(LibraryState state) => new(state);

    public static LibraryStateSessionOutcome Failure(string code, string explanation) =>
        new(null, code, explanation);
}

public sealed class LibraryStateChangedEventArgs(LibraryState state) : EventArgs
{
    public LibraryState State { get; } = state ?? throw new ArgumentNullException(nameof(state));
}

public interface ILibraryStateSession
{
    event EventHandler<LibraryStateChangedEventArgs>? StateChanged;

    Task<LibraryStateSessionOutcome> StartFromScanAsync(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken);

    Task<LibraryStateSessionOutcome> LoadAsync(
        string libraryRoot,
        CancellationToken cancellationToken);

    LibraryState? GetCurrent(string libraryRoot);

    IDisposable DeferStateChanged(string libraryRoot);

    Task<LibraryStateSessionOutcome> ApplyAsync(
        string libraryRoot,
        LibraryStateDelta delta,
        CancellationToken cancellationToken);

    Task<LibraryStateSessionOutcome> ApplyBatchAsync(
        string libraryRoot,
        IReadOnlyList<LibraryStateDelta> deltas,
        CancellationToken cancellationToken);

    Task<LibraryStateSessionOutcome> BeginMutationBatchAsync(
        string libraryRoot,
        LibraryStateMutationIntent intent,
        CancellationToken cancellationToken);

    Task<LibraryStateSessionOutcome> ApplyMutationBatchAsync(
        string libraryRoot,
        string mutationIntentId,
        IReadOnlyList<LibraryStateDelta> deltas,
        bool completeMutationIntent,
        CancellationToken cancellationToken);

    Task<LibraryStateSessionOutcome> CheckpointAsync(
        string libraryRoot,
        CancellationToken cancellationToken);

    Task<LibraryStateSessionOutcome> MarkUncertainAsync(
        string libraryRoot,
        LibraryStateUncertainty uncertainty,
        CancellationToken cancellationToken);
}

public sealed class LibraryStateSession(ILibraryStateStore? store = null) : ILibraryStateSession, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, LibraryState> _states = new(PathComparer());
    private readonly object _publicationGate = new();
    private readonly Dictionary<string, int> _publicationDeferrals = new(PathComparer());
    private readonly HashSet<string> _pendingPublications = new(PathComparer());

    public event EventHandler<LibraryStateChangedEventArgs>? StateChanged;

    public async Task<LibraryStateSessionOutcome> StartFromScanAsync(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LibraryState state = LibraryState.FromScan(snapshot, new(Guid.NewGuid()));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (store is not null)
                await store.WriteBaselineAsync(state, cancellationToken).ConfigureAwait(false);
            _states[snapshot.Identity.LibraryRoot] = state;
            return LibraryStateSessionOutcome.Success(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.BASELINE_PERSIST_FAILED", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LibraryStateSessionOutcome> LoadAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (store is null)
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.STORE_UNAVAILABLE", "No persistent library-state store is configured.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LibraryState? state = await store.ReadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
            if (state is null)
                return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.NOT_FOUND", "No persisted authoritative library state exists.");
            _states[state.Snapshot.Identity.LibraryRoot] = state;
            return LibraryStateSessionOutcome.Success(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.LOAD_FAILED", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public LibraryState? GetCurrent(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return _states.GetValueOrDefault(libraryRoot);
    }

    public IDisposable DeferStateChanged(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        lock (_publicationGate)
        {
            _publicationDeferrals[libraryRoot] = _publicationDeferrals.GetValueOrDefault(libraryRoot) + 1;
        }
        return new StatePublicationDeferral(this, libraryRoot);
    }

    public async Task<LibraryStateSessionOutcome> ApplyAsync(
        string libraryRoot,
        LibraryStateDelta delta,
        CancellationToken cancellationToken) => await ApplyBatchCoreAsync(
        libraryRoot, [delta], compactIfThresholdReached: true, null,
        completeMutationIntent: false, cancellationToken).ConfigureAwait(false);

    public async Task<LibraryStateSessionOutcome> ApplyBatchAsync(
        string libraryRoot,
        IReadOnlyList<LibraryStateDelta> deltas,
        CancellationToken cancellationToken) => await ApplyBatchCoreAsync(
        libraryRoot, deltas, compactIfThresholdReached: false, null,
        completeMutationIntent: false, cancellationToken).ConfigureAwait(false);

    public async Task<LibraryStateSessionOutcome> ApplyMutationBatchAsync(
        string libraryRoot,
        string mutationIntentId,
        IReadOnlyList<LibraryStateDelta> deltas,
        bool completeMutationIntent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationIntentId);
        return await ApplyBatchCoreAsync(libraryRoot, deltas, compactIfThresholdReached: false,
            mutationIntentId, completeMutationIntent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LibraryStateSessionOutcome> ApplyBatchCoreAsync(
        string libraryRoot,
        IReadOnlyList<LibraryStateDelta> deltas,
        bool compactIfThresholdReached,
        string? mutationIntentId,
        bool completeMutationIntent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(deltas);
        if (deltas.Count == 0) throw new ArgumentException("At least one state delta is required.", nameof(deltas));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_states.TryGetValue(libraryRoot, out LibraryState? current))
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.NOT_LOADED",
                    "Run an explicit library scan before applying state deltas.");
            string operationId = deltas[0].OperationId;
            try
            {
                LibraryState projected = LibraryStateDeltaPolicy.ApplyBatch(current, deltas);
                if (store is not null)
                    await store.AppendDeltaBatchAsync(libraryRoot, deltas, projected,
                        compactIfThresholdReached, mutationIntentId, completeMutationIntent,
                        cancellationToken).ConfigureAwait(false);
                _states[libraryRoot] = projected;
                PublishStateChanged(libraryRoot, projected);
                return LibraryStateSessionOutcome.Success(projected);
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or InvalidOperationException
                                               or OverflowException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
            {
                LibraryStateUncertainty uncertainty = new("DELTA_COMMIT_FAILED",
                    exception.Message, DateTimeOffset.UtcNow, operationId);
                LibraryState uncertain = current.MarkUncertain(uncertainty);
                _states[libraryRoot] = uncertain;
                PublishStateChanged(libraryRoot, uncertain);
                if (store is not null)
                {
                    try
                    {
                        await store.WriteUncertaintyAsync(libraryRoot, uncertain, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception persistException) when (persistException is IOException or UnauthorizedAccessException
                                                              or InvalidDataException or InvalidOperationException)
                    {
                        return LibraryStateSessionOutcome.Failure(
                            "LIBRARY_STATE.UNCERTAINTY_PERSIST_FAILED",
                            $"{exception.Message} Uncertainty persistence also failed: {persistException.Message}");
                    }
                }
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.DELTA_REJECTED",
                    exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LibraryStateSessionOutcome> BeginMutationBatchAsync(
        string libraryRoot,
        LibraryStateMutationIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(intent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_states.TryGetValue(libraryRoot, out LibraryState? current))
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.NOT_LOADED", "No library state exists for a mutation intent.");
            if (!current.IsAuthoritative || intent.GenerationId != current.GenerationId
                || intent.ExpectedRevision != current.Revision)
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.INTENT_REJECTED", "The mutation intent does not match authoritative state.");
            if (store is not null)
                await store.WriteMutationIntentAsync(libraryRoot, intent, cancellationToken).ConfigureAwait(false);
            return LibraryStateSessionOutcome.Success(current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.INTENT_PERSIST_FAILED", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LibraryStateSessionOutcome> CheckpointAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_states.TryGetValue(libraryRoot, out LibraryState? current))
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.NOT_LOADED", "No library state exists to checkpoint.");
            if (store is not null)
                await store.CompactAsync(libraryRoot, current, cancellationToken).ConfigureAwait(false);
            return LibraryStateSessionOutcome.Success(current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.CHECKPOINT_FAILED", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LibraryStateSessionOutcome> MarkUncertainAsync(
        string libraryRoot,
        LibraryStateUncertainty uncertainty,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(uncertainty);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_states.TryGetValue(libraryRoot, out LibraryState? current))
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.NOT_LOADED",
                    "No authoritative library state exists to mark uncertain.");
            LibraryState uncertain = current.MarkUncertain(uncertainty);
            if (store is not null)
                await store.WriteUncertaintyAsync(libraryRoot, uncertain, cancellationToken).ConfigureAwait(false);
            _states[libraryRoot] = uncertain;
            PublishStateChanged(libraryRoot, uncertain);
            return LibraryStateSessionOutcome.Success(uncertain);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.UNCERTAINTY_PERSIST_FAILED", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void PublishStateChanged(string libraryRoot, LibraryState state)
    {
        lock (_publicationGate)
        {
            if (_publicationDeferrals.ContainsKey(libraryRoot))
            {
                _pendingPublications.Add(libraryRoot);
                return;
            }
        }
        StateChanged?.Invoke(this, new(state));
    }

    private void EndStateChangedDeferral(string libraryRoot)
    {
        LibraryState? pending = null;
        lock (_publicationGate)
        {
            if (!_publicationDeferrals.TryGetValue(libraryRoot, out int count)) return;
            if (count > 1)
            {
                _publicationDeferrals[libraryRoot] = count - 1;
                return;
            }
            _publicationDeferrals.Remove(libraryRoot);
            if (_pendingPublications.Remove(libraryRoot)) pending = _states.GetValueOrDefault(libraryRoot);
        }
        if (pending is not null) StateChanged?.Invoke(this, new(pending));
    }

    public void Dispose() => _gate.Dispose();

    private sealed class StatePublicationDeferral(LibraryStateSession owner, string libraryRoot) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndStateChangedDeferral(libraryRoot);
        }
    }
}
