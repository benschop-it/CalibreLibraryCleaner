namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryValidationOutcome
{
    private LibraryValidationOutcome(ValidatedLibraryLocation? location, LibraryError? error)
    {
        Location = location;
        Error = error;
    }

    public bool IsSuccess => Location is not null;

    public ValidatedLibraryLocation? Location { get; }

    public LibraryError? Error { get; }

    public static LibraryValidationOutcome Success(ValidatedLibraryLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new(location, null);
    }

    public static LibraryValidationOutcome Failure(LibraryError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(null, error);
    }
}
