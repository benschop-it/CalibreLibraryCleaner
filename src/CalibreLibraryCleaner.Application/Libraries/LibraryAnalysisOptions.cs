namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryAnalysisOptions
{
    public LibraryAnalysisOptions(int maxHashConcurrency = 4, int maxEpubAssessmentConcurrency = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHashConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEpubAssessmentConcurrency);
        MaxHashConcurrency = maxHashConcurrency;
        MaxEpubAssessmentConcurrency = maxEpubAssessmentConcurrency;
    }

    public int MaxHashConcurrency { get; }

    public int MaxEpubAssessmentConcurrency { get; }
}
