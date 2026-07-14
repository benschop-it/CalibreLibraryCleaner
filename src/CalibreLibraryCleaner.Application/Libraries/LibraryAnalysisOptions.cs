namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryAnalysisOptions
{
    public LibraryAnalysisOptions(int maxHashConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHashConcurrency);
        MaxHashConcurrency = maxHashConcurrency;
    }

    public int MaxHashConcurrency { get; }
}
