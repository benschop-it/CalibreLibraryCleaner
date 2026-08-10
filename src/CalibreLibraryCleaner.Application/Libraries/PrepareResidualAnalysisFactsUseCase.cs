using System.Collections.ObjectModel;
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
        CancellationToken cancellationToken);

    Task<CandidateContentSignatureBatchResult> ResolveCandidateContentSignaturesAsync(
        ResidualAnalysisFacts facts,
        IReadOnlyList<BookCandidatePair> candidatePairs,
        IProgress<CandidateContentSignatureProgress>? progress,
        CancellationToken cancellationToken);
}

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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(epubTargets);
        ArgumentNullException.ThrowIfNull(pdfTargets);
        cancellationToken.ThrowIfCancellationRequested();
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
        IReadOnlyList<EpubAssessment> freshEpub = epubReuse.FreshTargets.Count == 0
            ? []
            : await assessEpubFormats.ExecuteAsync(
                epubReuse.FreshTargets,
                options.MaxEpubAssessmentConcurrency,
                EpubInspectionLimits.V1,
                null,
                cancellationToken).ConfigureAwait(false);
        PdfAssessmentReuseResult pdfReuse = AssessmentReusePolicy.PartitionPdf(reusable, pdfTargets);
        IReadOnlyList<PdfAssessment> freshPdf = pdfReuse.FreshTargets.Count == 0
            ? []
            : await assessPdfFormats.ExecuteAsync(
                pdfReuse.FreshTargets,
                options.MaxPdfAssessmentConcurrency,
                PdfInspectionLimits.V1,
                null,
                cancellationToken).ConfigureAwait(false);
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
            freshPdf.Count);
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

    [LoggerMessage(2, LogLevel.Information,
        "Residual format facts prepared. EpubTargets={EpubTargets}, ReusedEpubAssessments={ReusedEpubAssessments}, FreshEpubAssessments={FreshEpubAssessments}, PdfTargets={PdfTargets}, ReusedPdfAssessments={ReusedPdfAssessments}, FreshPdfAssessments={FreshPdfAssessments}.")]
    private static partial void LogAssessmentPreparationCompleted(
        ILogger logger,
        int epubTargets,
        int reusedEpubAssessments,
        int freshEpubAssessments,
        int pdfTargets,
        int reusedPdfAssessments,
        int freshPdfAssessments);
}
