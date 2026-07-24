namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryError(
    LibraryErrorCode Code,
    string Message,
    string SuggestedAction);
