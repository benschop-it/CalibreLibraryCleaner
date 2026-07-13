using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ILibraryPathResolver
{
    Task<LibraryValidationOutcome> ValidateAsync(
        string? candidatePath,
        CancellationToken cancellationToken);

    ResolvedFormatPathOutcome ResolveFormat(
        ValidatedLibraryLocation library,
        string relativeDirectory,
        string storedName,
        string format);

    ValueTask<bool> FileExistsAsync(
        ResolvedFormatPath path,
        CancellationToken cancellationToken);
}
