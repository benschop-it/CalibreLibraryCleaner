namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryAnalysisOptions
{
    public LibraryAnalysisOptions(
        int maxHashConcurrency = 4,
        int maxEpubAssessmentConcurrency = 2,
        int maxPdfAssessmentConcurrency = 2,
        int maxContentSignatureConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHashConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEpubAssessmentConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPdfAssessmentConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContentSignatureConcurrency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPdfAssessmentConcurrency, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxContentSignatureConcurrency, 4);
        MaxHashConcurrency = maxHashConcurrency;
        MaxEpubAssessmentConcurrency = maxEpubAssessmentConcurrency;
        MaxPdfAssessmentConcurrency = maxPdfAssessmentConcurrency;
        MaxContentSignatureConcurrency = maxContentSignatureConcurrency;
    }

    public int MaxHashConcurrency { get; }

    public int MaxEpubAssessmentConcurrency { get; }

    public int MaxPdfAssessmentConcurrency { get; }

    public int MaxContentSignatureConcurrency { get; }
}
