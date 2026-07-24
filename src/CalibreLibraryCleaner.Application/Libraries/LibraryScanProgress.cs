namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryScanProgress(
    LibraryScanPhase Phase,
    long CompletedUnits,
    long? TotalUnits,
    string Message);
