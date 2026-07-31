namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryAnalysisOptions
{
    public LibraryAnalysisOptions(
        int maxHashConcurrency = 4,
        int maxEpubAssessmentConcurrency = 2,
        int maxPdfAssessmentConcurrency = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHashConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEpubAssessmentConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPdfAssessmentConcurrency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPdfAssessmentConcurrency, 8);
        MaxHashConcurrency = maxHashConcurrency;
        MaxEpubAssessmentConcurrency = maxEpubAssessmentConcurrency;
        MaxPdfAssessmentConcurrency = maxPdfAssessmentConcurrency;
    }

    public int MaxHashConcurrency { get; }

    public int MaxEpubAssessmentConcurrency { get; }

    public int MaxPdfAssessmentConcurrency { get; }
}
