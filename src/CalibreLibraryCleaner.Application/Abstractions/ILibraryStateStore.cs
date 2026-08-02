using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ILibraryStateStore
{
    Task WriteBaselineAsync(LibraryState state, CancellationToken cancellationToken);

    Task AppendDeltaAsync(
        string libraryRoot,
        LibraryStateDelta delta,
        LibraryState projectedState,
        CancellationToken cancellationToken);

    Task WriteUncertaintyAsync(
        string libraryRoot,
        LibraryState state,
        CancellationToken cancellationToken);

    Task<LibraryState?> ReadAsync(string libraryRoot, CancellationToken cancellationToken);
}
