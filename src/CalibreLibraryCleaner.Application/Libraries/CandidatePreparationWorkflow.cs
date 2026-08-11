namespace CalibreLibraryCleaner.Application.Libraries;

public enum CandidatePreparationPhase
{
    LoadingState,
    ReadingCatalog,
    Reconciling,
    ResolvingFiles,
    HashingTransferTargets,
    AssessingEpubFormats,
    AssessingPdfFormats,
    PublishingRefreshedState,
    ResolvingCandidateTargets,
    DetectingExactMetadata,
    DiscoveringExpandedCandidates,
    MergingUnifiedCandidates,
    PublishingCandidateAnalysis,
    PreparingPresentation,
    Completed,
}

public enum CandidateProgressUnit
{
    Steps,
    Records,
    Files,
    Bytes,
    Pages,
    Fingerprints,
    Groups,
}

public sealed record CandidatePreparationProgress
{
    public const int MaximumTextLength = 512;

    public CandidatePreparationProgress(
        CandidatePreparationPhase phase,
        long completed,
        long total,
        CandidateProgressUnit unit,
        string message,
        string detail = "",
        int activeItems = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfNegative(activeItems);
        if (total > 0 && completed > total)
            throw new ArgumentException("Candidate preparation progress cannot exceed its phase total.");
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaximumTextLength || (detail?.Length ?? 0) > MaximumTextLength)
            throw new ArgumentException("Candidate preparation progress text exceeds its presentation bound.");
        if (!Enum.IsDefined(phase) || !Enum.IsDefined(unit))
            throw new ArgumentOutOfRangeException(nameof(phase));
        Phase = phase;
        Completed = completed;
        Total = total;
        Unit = unit;
        Message = message;
        Detail = detail ?? string.Empty;
        ActiveItems = activeItems;
    }

    public CandidatePreparationPhase Phase { get; }
    public long Completed { get; }
    public long Total { get; }
    public CandidateProgressUnit Unit { get; }
    public string Message { get; }
    public string Detail { get; }
    public int ActiveItems { get; }
}

public interface ICandidatePreparationWorkflow
{
    Task<PostExactRefreshResult> RefreshAsync(
        string libraryRoot,
        IProgress<CandidatePreparationProgress>? progress,
        CancellationToken cancellationToken);

    Task<ResidualCandidateAnalysisResult> AnalyzeAsync(
        string libraryRoot,
        IProgress<CandidatePreparationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class CandidatePreparationWorkflow(
    RefreshAfterExactCleanupUseCase refresh,
    AnalyzeResidualCandidatesUseCase analyze) : ICandidatePreparationWorkflow
{
    public Task<PostExactRefreshResult> RefreshAsync(
        string libraryRoot,
        IProgress<CandidatePreparationProgress>? progress,
        CancellationToken cancellationToken) => refresh.ExecuteAsync(
        libraryRoot,
        progress is null ? null : new RefreshProgressAdapter(progress),
        cancellationToken);

    public Task<ResidualCandidateAnalysisResult> AnalyzeAsync(
        string libraryRoot,
        IProgress<CandidatePreparationProgress>? progress,
        CancellationToken cancellationToken) => analyze.ExecuteAsync(
        libraryRoot,
        progress is null ? null : new AnalysisProgressAdapter(progress),
        cancellationToken);

    private sealed class RefreshProgressAdapter(IProgress<CandidatePreparationProgress> progress) :
        IProgress<PostExactRefreshProgress>
    {
        public void Report(PostExactRefreshProgress value) => progress.Report(new(
            value.Phase switch
            {
                PostExactRefreshPhase.LoadingState => CandidatePreparationPhase.LoadingState,
                PostExactRefreshPhase.ReadingCatalog => CandidatePreparationPhase.ReadingCatalog,
                PostExactRefreshPhase.Reconciling => CandidatePreparationPhase.Reconciling,
                PostExactRefreshPhase.ResolvingFiles => CandidatePreparationPhase.ResolvingFiles,
                PostExactRefreshPhase.HashingTransferTargets => CandidatePreparationPhase.HashingTransferTargets,
                PostExactRefreshPhase.AssessingEpubFormats => CandidatePreparationPhase.AssessingEpubFormats,
                PostExactRefreshPhase.AssessingPdfFormats => CandidatePreparationPhase.AssessingPdfFormats,
                PostExactRefreshPhase.Publishing => CandidatePreparationPhase.PublishingRefreshedState,
                PostExactRefreshPhase.Completed => CandidatePreparationPhase.Completed,
                _ => throw new InvalidOperationException("Unsupported post-Exact progress phase."),
            },
            value.Completed,
            value.Total,
            value.Unit,
            value.Message,
            value.Detail,
            value.ActiveItems));
    }

    private sealed class AnalysisProgressAdapter(IProgress<CandidatePreparationProgress> progress) :
        IProgress<ResidualCandidateAnalysisProgress>
    {
        public void Report(ResidualCandidateAnalysisProgress value) => progress.Report(new(
            value.Phase switch
            {
                ResidualCandidateAnalysisPhase.LoadingState => CandidatePreparationPhase.LoadingState,
                ResidualCandidateAnalysisPhase.ResolvingEpubTargets => CandidatePreparationPhase.ResolvingCandidateTargets,
                ResidualCandidateAnalysisPhase.DetectingExactMetadata => CandidatePreparationPhase.DetectingExactMetadata,
                ResidualCandidateAnalysisPhase.DiscoveringExpandedCandidates => CandidatePreparationPhase.DiscoveringExpandedCandidates,
                ResidualCandidateAnalysisPhase.MergingUnifiedCandidates => CandidatePreparationPhase.MergingUnifiedCandidates,
                ResidualCandidateAnalysisPhase.Publishing => CandidatePreparationPhase.PublishingCandidateAnalysis,
                ResidualCandidateAnalysisPhase.Completed => CandidatePreparationPhase.Completed,
                _ => throw new InvalidOperationException("Unsupported residual analysis progress phase."),
            },
            value.Completed,
            value.Total,
            value.Unit,
            value.Message,
            value.Detail,
            value.ActiveItems));
    }
}
