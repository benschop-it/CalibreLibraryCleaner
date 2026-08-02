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

    Task<LibraryStateSessionOutcome> ApplyAsync(
        string libraryRoot,
        LibraryStateDelta delta,
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

    public async Task<LibraryStateSessionOutcome> ApplyAsync(
        string libraryRoot,
        LibraryStateDelta delta,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(delta);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_states.TryGetValue(libraryRoot, out LibraryState? current))
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.NOT_LOADED",
                    "Run an explicit library scan before applying state deltas.");
            try
            {
                LibraryState projected = LibraryStateDeltaPolicy.Apply(current, delta);
                if (store is not null)
                    await store.AppendDeltaAsync(libraryRoot, delta, projected, cancellationToken).ConfigureAwait(false);
                _states[libraryRoot] = projected;
                StateChanged?.Invoke(this, new(projected));
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
                    exception.Message, DateTimeOffset.UtcNow, delta.OperationId);
                LibraryState uncertain = current.MarkUncertain(uncertainty);
                _states[libraryRoot] = uncertain;
                StateChanged?.Invoke(this, new(uncertain));
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
            StateChanged?.Invoke(this, new(uncertain));
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

    public void Dispose() => _gate.Dispose();
}
