using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed record RecommendationSourceOptionViewModel(
    long? BookId,
    string Label,
    string Action = "SelectSource");

public sealed class RecommendationFormatRowViewModel : ObservableObject
{
    private RecommendationSourceOptionViewModel? _reviewedSource;

    public RecommendationFormatRowViewModel(
        string format,
        string generatedSource,
        string resolution,
        string strength,
        string candidates,
        string exactBinary,
        string epubAssessment,
        string warnings,
        IReadOnlyList<RecommendationSourceOptionViewModel> sourceOptions,
        RecommendationSourceOptionViewModel? reviewedSource)
    {
        Format = format;
        GeneratedSource = generatedSource;
        Resolution = resolution;
        Strength = strength;
        Candidates = candidates;
        ExactBinary = exactBinary;
        EpubAssessment = epubAssessment;
        Warnings = warnings;
        SourceOptions = sourceOptions;
        _reviewedSource = reviewedSource;
    }

    public string Format { get; }
    public string GeneratedSource { get; }
    public string Resolution { get; }
    public string Strength { get; }
    public string Candidates { get; }
    public string ExactBinary { get; }
    public string EpubAssessment { get; }
    public string Warnings { get; }
    public IReadOnlyList<RecommendationSourceOptionViewModel> SourceOptions { get; }

    public RecommendationSourceOptionViewModel? ReviewedSource
    {
        get => _reviewedSource;
        set => SetProperty(ref _reviewedSource, value);
    }
}

public sealed record RecommendationReasonRowViewModel(
    string Code,
    string Subject,
    string Explanation,
    string Evidence);

public sealed record RecommendationWarningRowViewModel(
    string Code,
    string Severity,
    string Subject,
    string Explanation,
    string Evidence);
