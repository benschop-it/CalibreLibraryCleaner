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

public interface ILibraryStateSession
{
    LibraryStateSessionOutcome StartFromScan(
        LibrarySnapshot snapshot);

    LibraryState? GetCurrent(string libraryRoot);

    LibraryStateSessionOutcome Apply(string libraryRoot, LibraryStateDelta delta);

    LibraryStateSessionOutcome MarkUncertain(
        string libraryRoot,
        LibraryStateUncertainty uncertainty);
}

public sealed class LibraryStateSession : ILibraryStateSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LibraryState> _states = new(PathComparer());

    public LibraryStateSessionOutcome StartFromScan(
        LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LibraryState state = LibraryState.FromScan(snapshot, new(Guid.NewGuid()));
        lock (_gate)
        {
            _states[snapshot.Identity.LibraryRoot] = state;
        }
        return LibraryStateSessionOutcome.Success(state);
    }

    public LibraryState? GetCurrent(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        lock (_gate)
        {
            return _states.GetValueOrDefault(libraryRoot);
        }
    }

    public LibraryStateSessionOutcome Apply(string libraryRoot, LibraryStateDelta delta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(delta);
        lock (_gate)
        {
            if (!_states.TryGetValue(libraryRoot, out LibraryState? current))
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.NOT_LOADED",
                    "Run an explicit library scan before applying state deltas.");
            try
            {
                LibraryState projected = LibraryStateDeltaPolicy.Apply(current, delta);
                _states[libraryRoot] = projected;
                return LibraryStateSessionOutcome.Success(projected);
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or InvalidOperationException
                                               or OverflowException)
            {
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.DELTA_REJECTED",
                    exception.Message);
            }
        }
    }

    public LibraryStateSessionOutcome MarkUncertain(
        string libraryRoot,
        LibraryStateUncertainty uncertainty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(uncertainty);
        lock (_gate)
        {
            if (!_states.TryGetValue(libraryRoot, out LibraryState? current))
                return LibraryStateSessionOutcome.Failure(
                    "LIBRARY_STATE.NOT_LOADED",
                    "No authoritative library state exists to mark uncertain.");
            LibraryState uncertain = current.MarkUncertain(uncertainty);
            _states[libraryRoot] = uncertain;
            return LibraryStateSessionOutcome.Success(uncertain);
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
