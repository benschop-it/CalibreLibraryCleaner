using CalibreLibraryCleaner.Application.Abstractions;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed class ValidateLibraryUseCase(ILibraryPathResolver pathResolver)
{
    public Task<LibraryValidationOutcome> ExecuteAsync(
        string? candidatePath,
        CancellationToken cancellationToken) =>
        pathResolver.ValidateAsync(candidatePath, cancellationToken);
}
