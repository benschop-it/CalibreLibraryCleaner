using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryScanOutcome
{
    private LibraryScanOutcome(LibrarySnapshot? snapshot, LibraryError? error)
    {
        Snapshot = snapshot;
        Error = error;
    }

    public bool IsSuccess => Snapshot is not null;

    public LibrarySnapshot? Snapshot { get; }

    public LibraryError? Error { get; }

    public static LibraryScanOutcome Success(LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot, null);
    }

    public static LibraryScanOutcome Failure(LibraryError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(null, error);
    }
}
