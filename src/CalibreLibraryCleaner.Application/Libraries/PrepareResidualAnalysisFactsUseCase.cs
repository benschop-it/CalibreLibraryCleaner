using System.Collections.ObjectModel;
using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record ResidualAnalysisFacts
{
    public ResidualAnalysisFacts(
        IEnumerable<EpubAssessmentTarget> epubTargets,
        IEnumerable<PdfAssessmentTarget> pdfTargets,
        IEnumerable<EpubAssessment> epubAssessments,
        IEnumerable<PdfAssessment> pdfAssessments,
        int reusedEpubAssessmentCount,
        int freshEpubAssessmentCount,
        int reusedPdfAssessmentCount,
        int freshPdfAssessmentCount)
    {
        EpubTargets = new ReadOnlyCollection<EpubAssessmentTarget>(epubTargets.ToArray());
        PdfTargets = new ReadOnlyCollection<PdfAssessmentTarget>(pdfTargets.ToArray());
        EpubAssessments = new ReadOnlyCollection<EpubAssessment>(epubAssessments.ToArray());
        PdfAssessments = new ReadOnlyCollection<PdfAssessment>(pdfAssessments.ToArray());
        ReusedEpubAssessmentCount = reusedEpubAssessmentCount;
        FreshEpubAssessmentCount = freshEpubAssessmentCount;
        ReusedPdfAssessmentCount = reusedPdfAssessmentCount;
        FreshPdfAssessmentCount = freshPdfAssessmentCount;
    }

    public IReadOnlyList<EpubAssessmentTarget> EpubTargets { get; }
    public IReadOnlyList<PdfAssessmentTarget> PdfTargets { get; }
    public IReadOnlyList<EpubAssessment> EpubAssessments { get; }
    public IReadOnlyList<PdfAssessment> PdfAssessments { get; }
    public int ReusedEpubAssessmentCount { get; }
    public int FreshEpubAssessmentCount { get; }
    public int ReusedPdfAssessmentCount { get; }
    public int FreshPdfAssessmentCount { get; }
}

public interface IResidualAnalysisFactsPreparer
{
    Task<ResidualAnalysisFacts> PrepareAsync(
        string libraryRoot,
        IReadOnlyList<EpubAssessmentTarget> epubTargets,
        IReadOnlyList<PdfAssessmentTarget> pdfTargets,
        IProgress<ResidualAnalysisFactsProgress>? progress,
        CancellationToken cancellationToken);

    Task<CandidateContentSignatureBatchResult> ResolveCandidateContentSignaturesAsync(
        ResidualAnalysisFacts facts,
        IReadOnlyList<BookCandidatePair> candidatePairs,
        IProgress<CandidateContentSignatureProgress>? progress,
        CancellationToken cancellationToken);
}

public enum ResidualAnalysisFactsPhase
{
    AssessingEpubFormats,
    AssessingPdfFormats,
}

public sealed record ResidualAnalysisFactsProgress(
    ResidualAnalysisFactsPhase Phase,
    int CompletedFiles,
    int TotalFiles,
    int ReusedFiles,
    int ActiveFiles,
    string Stage,
    int CompletedPages = 0,
    int TotalPages = 0);

public sealed partial class PrepareResidualAnalysisFactsUseCase(
    ILibraryStateStore stateStore,
    AssessEpubFormatsUseCase assessEpubFormats,
    AssessPdfFormatsUseCase assessPdfFormats,
    ResolveCandidateContentSignaturesUseCase resolveContentSignatures,
    LibraryAnalysisOptions options,
    ILogger<PrepareResidualAnalysisFactsUseCase>? logger = null) : IResidualAnalysisFactsPreparer
{
    private readonly ILogger<PrepareResidualAnalysisFactsUseCase> _logger = logger
        ?? NullLogger<PrepareResidualAnalysisFactsUseCase>.Instance;

    public async Task<ResidualAnalysisFacts> PrepareAsync(
        string libraryRoot,
        IReadOnlyList<EpubAssessmentTarget> epubTargets,
        IReadOnlyList<PdfAssessmentTarget> pdfTargets,
        IProgress<ResidualAnalysisFactsProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(epubTargets);
        ArgumentNullException.ThrowIfNull(pdfTargets);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        LibrarySnapshot? reusable = null;
        try
        {
            reusable = await stateStore.ReadReusableAssessmentSnapshotAsync(
                libraryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException)
        {
            LogAssessmentCacheUnavailable(_logger, exception.GetType().Name);
        }

        EpubAssessmentReuseResult epubReuse = AssessmentReusePolicy.PartitionEpub(reusable, epubTargets);
        progress?.Report(new(
            ResidualAnalysisFactsPhase.AssessingEpubFormats,
            epubReuse.Reused.Count,
            epubTargets.Count,
            epubReuse.Reused.Count,
            0,
            "Starting"));
        long epubStarted = Stopwatch.GetTimestamp();
        IReadOnlyList<EpubAssessment> freshEpub = epubReuse.FreshTargets.Count == 0
            ? []
            : await assessEpubFormats.ExecuteAsync(
                epubReuse.FreshTargets,
                options.MaxEpubAssessmentConcurrency,
                EpubInspectionLimits.V1,
                progress is null
                    ? null
                    : new EpubProgressAdapter(progress, epubReuse.Reused.Count, epubTargets.Count),
                cancellationToken).ConfigureAwait(false);
        long epubCompleted = Stopwatch.GetTimestamp();
        PdfAssessmentReuseResult pdfReuse = AssessmentReusePolicy.PartitionPdf(reusable, pdfTargets);
        progress?.Report(new(
            ResidualAnalysisFactsPhase.AssessingPdfFormats,
            pdfReuse.Reused.Count,
            pdfTargets.Count,
            pdfReuse.Reused.Count,
            0,
            "Starting"));
        IReadOnlyList<PdfAssessment> freshPdf = pdfReuse.FreshTargets.Count == 0
            ? []
            : await assessPdfFormats.ExecuteAsync(
                pdfReuse.FreshTargets,
                options.MaxPdfAssessmentConcurrency,
                PdfInspectionLimits.V1,
                progress is null
                    ? null
                    : new PdfProgressAdapter(progress, pdfReuse.Reused.Count, pdfTargets.Count),
                cancellationToken).ConfigureAwait(false);
        long pdfCompleted = Stopwatch.GetTimestamp();
        EpubAssessment[] epub = epubReuse.Reused.Concat(freshEpub)
            .OrderBy(value => value.CalibreBookId.Value)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        PdfAssessment[] pdf = pdfReuse.Reused.Concat(freshPdf)
            .OrderBy(value => value.CalibreBookId.Value)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        LogAssessmentPreparationCompleted(
            _logger,
            epubTargets.Count,
            epubReuse.Reused.Count,
            freshEpub.Count,
            pdfTargets.Count,
            pdfReuse.Reused.Count,
            freshPdf.Count,
            ElapsedMilliseconds(epubStarted, epubCompleted),
            ElapsedMilliseconds(epubCompleted, pdfCompleted),
            ElapsedMilliseconds(started, pdfCompleted));
        return new(
            epubTargets,
            pdfTargets,
            epub,
            pdf,
            epubReuse.Reused.Count,
            freshEpub.Count,
            pdfReuse.Reused.Count,
            freshPdf.Count);
    }

    public Task<CandidateContentSignatureBatchResult> ResolveCandidateContentSignaturesAsync(
        ResidualAnalysisFacts facts,
        IReadOnlyList<BookCandidatePair> candidatePairs,
        IProgress<CandidateContentSignatureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return resolveContentSignatures.ExecuteAsync(
            candidatePairs,
            facts.EpubTargets,
            options.MaxContentSignatureConcurrency,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            progress,
            cancellationToken);
    }

    [LoggerMessage(1, LogLevel.Warning,
        "Reusable assessment snapshot could not be loaded. FailureType={FailureType}.")]
    private static partial void LogAssessmentCacheUnavailable(ILogger logger, string failureType);

    private static long ElapsedMilliseconds(long started, long completed) =>
        (long)Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;

    [LoggerMessage(2, LogLevel.Information,
        "Residual format facts prepared. EpubTargets={EpubTargets}, ReusedEpubAssessments={ReusedEpubAssessments}, FreshEpubAssessments={FreshEpubAssessments}, PdfTargets={PdfTargets}, ReusedPdfAssessments={ReusedPdfAssessments}, FreshPdfAssessments={FreshPdfAssessments}, EpubMilliseconds={EpubMilliseconds}, PdfMilliseconds={PdfMilliseconds}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogAssessmentPreparationCompleted(
        ILogger logger,
        int epubTargets,
        int reusedEpubAssessments,
        int freshEpubAssessments,
        int pdfTargets,
        int reusedPdfAssessments,
        int freshPdfAssessments,
        long epubMilliseconds,
        long pdfMilliseconds,
        long totalMilliseconds);

    private sealed class EpubProgressAdapter(
        IProgress<ResidualAnalysisFactsProgress> progress,
        int reusedFiles,
        int totalFiles) : IProgress<EpubAssessmentProgress>
    {
        public void Report(EpubAssessmentProgress value) => progress.Report(new(
            ResidualAnalysisFactsPhase.AssessingEpubFormats,
            reusedFiles + value.CompletedFiles,
            totalFiles,
            reusedFiles,
            value.ActiveFiles,
            value.ActiveStageSummary.Length > 0 ? value.ActiveStageSummary : value.Stage));
    }

    private sealed class PdfProgressAdapter(
        IProgress<ResidualAnalysisFactsProgress> progress,
        int reusedFiles,
        int totalFiles) : IProgress<PdfAssessmentProgress>
    {
        public void Report(PdfAssessmentProgress value) => progress.Report(new(
            ResidualAnalysisFactsPhase.AssessingPdfFormats,
            reusedFiles + value.CompletedFiles,
            totalFiles,
            reusedFiles,
            string.Equals(value.Stage, "Complete", StringComparison.Ordinal) ? 0 : 1,
            value.Stage,
            value.CompletedPages,
            value.TotalPages));
    }
}
