namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryAnalysisOptions
{
    public LibraryAnalysisOptions(
        int maxHashConcurrency = 4,
        int? maxEpubAssessmentConcurrency = null,
        int maxPdfAssessmentConcurrency = 2,
        int maxContentSignatureConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHashConcurrency);
        int epubConcurrency = maxEpubAssessmentConcurrency
            ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epubConcurrency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(epubConcurrency, 4);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPdfAssessmentConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContentSignatureConcurrency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPdfAssessmentConcurrency, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxContentSignatureConcurrency, 4);
        MaxHashConcurrency = maxHashConcurrency;
        MaxEpubAssessmentConcurrency = epubConcurrency;
        MaxPdfAssessmentConcurrency = maxPdfAssessmentConcurrency;
        MaxContentSignatureConcurrency = maxContentSignatureConcurrency;
    }

    public int MaxHashConcurrency { get; }

    public int MaxEpubAssessmentConcurrency { get; }

    public int MaxPdfAssessmentConcurrency { get; }

    public int MaxContentSignatureConcurrency { get; }
}
