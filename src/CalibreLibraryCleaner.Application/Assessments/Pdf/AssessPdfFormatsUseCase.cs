using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Assessments.Pdf;

public sealed class AssessPdfFormatsUseCase(
    IPdfInspector inspector,
    PdfPageSamplingPolicy samplingPolicy,
    PdfAssessmentEngine engine)
{
    public async Task<IReadOnlyList<PdfAssessment>> ExecuteAsync(
        IReadOnlyList<PdfAssessmentTarget> allTargets,
        int maxConcurrency,
        PdfInspectionLimits limits,
        IProgress<PdfAssessmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allTargets);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrency, 8);
        limits.Validate();
        if (limits != PdfInspectionLimits.V1)
        {
            throw new ArgumentException(
                "Published PDF assessments require the exact frozen V1 resource profile.",
                nameof(limits));
        }
        cancellationToken.ThrowIfCancellationRequested();
        PdfAssessmentTarget[] targets = allTargets
            .Where(target => string.Equals(target.Format, "PDF", StringComparison.OrdinalIgnoreCase))
            .OrderBy(target => target.BookId.Value)
            .ThenBy(target => target.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        PdfAssessment?[] results = new PdfAssessment?[targets.Length];
        int publishedCompleted = 0;
        int nextProgressIndex = 0;
        object progressGate = new();
        progress?.Report(new(0, targets.Length, string.Empty, "Starting"));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Length),
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                token.ThrowIfCancellationRequested();
                PdfAssessmentTarget target = targets[index];
                string safePath = SafePath(target);
                PdfInspectionResult inspection;
                if (target.FileStatus != FormatFileStatus.Present)
                {
                    PdfInspectionProblemCode problem = target.FileStatus switch
                    {
                        FormatFileStatus.Missing => PdfInspectionProblemCode.MissingFile,
                        FormatFileStatus.InvalidPath => PdfInspectionProblemCode.UnsafePath,
                        FormatFileStatus.Inaccessible => PdfInspectionProblemCode.InaccessibleFile,
                        FormatFileStatus.ChangedDuringHashing => PdfInspectionProblemCode.ChangedFile,
                        _ => PdfInspectionProblemCode.InaccessibleFile,
                    };
                    inspection = PdfInspectionResult.Failed(target.BookId, safePath, problem);
                }
                else
                {
                    if (target.Fingerprint is null
                        || target.Observation is null
                        || target.Fingerprint.SizeInBytes != target.Observation.Length
                        || string.IsNullOrWhiteSpace(target.LibraryRoot)
                        || string.IsNullOrWhiteSpace(target.FullPath))
                    {
                        throw new InvalidOperationException("A present PDF target requires one consistent verified file identity.");
                    }

                    PdfInspectionRequest request = new(
                        target.BookId,
                        target.LibraryRoot,
                        target.FullPath,
                        safePath,
                        target.Fingerprint,
                        target.Observation,
                        limits);
                    IProgress<PdfInspectionProgress>? pageProgress = progress is null
                        ? null
                        : CreateCoalescedProgress(value =>
                        {
                            lock (progressGate)
                            {
                                if (index != nextProgressIndex)
                                {
                                    return;
                                }

                                progress.Report(new(
                                    publishedCompleted,
                                    targets.Length,
                                    safePath,
                                    value.Stage,
                                    value.CompletedPages,
                                    value.TotalPages));
                            }
                        });
                    inspection = await inspector.InspectAsync(
                        request,
                        (header, callbackToken) => SelectPagesAsync(request, header, callbackToken),
                        pageProgress,
                        token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                results[index] = engine.Assess(target.BookId, safePath, target.Fingerprint, inspection, target.Observation, token);
                lock (progressGate)
                {
                    while (nextProgressIndex < results.Length && results[nextProgressIndex] is not null)
                    {
                        PdfAssessmentTarget publishedTarget = targets[nextProgressIndex];
                        publishedCompleted++;
                        nextProgressIndex++;
                        progress?.Report(new(
                            publishedCompleted,
                            targets.Length,
                            SafePath(publishedTarget),
                            "Complete"));
                    }
                }
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return results.Select(result => result ?? throw new InvalidOperationException("A PDF assessment result is missing.")).ToArray();
    }

    private ValueTask<IReadOnlyList<int>> SelectPagesAsync(
        PdfInspectionRequest request,
        PdfDocumentHeaderFacts header,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (header.BookId != request.BookId
            || !string.Equals(header.ExpectedRelativePath, request.ExpectedRelativePath, StringComparison.Ordinal)
            || header.PageCount <= 0
            || header.PageCount > request.Limits.MaximumPages
            || header.OutlineTargetPages.Count > request.Limits.MaximumOutlineNodes)
        {
            throw new InvalidOperationException("The PDF inspector returned invalid header facts.");
        }

        IReadOnlyList<int> pages = samplingPolicy.Select(header.PageCount, header.OutlineTargetPages);
        if (pages.Count > request.Limits.MaximumSampledPages)
        {
            throw new InvalidOperationException("The Application sampling policy exceeded the requested PDF limit.");
        }

        return ValueTask.FromResult(pages);
    }

    private static string SafePath(PdfAssessmentTarget target) => string.IsNullOrWhiteSpace(target.ExpectedRelativePath)
        ? $"invalid-path/book-{target.BookId.Value}.pdf"
        : target.ExpectedRelativePath.Replace('\\', '/');

    private static InlineProgress CreateCoalescedProgress(Action<PdfInspectionProgress> report)
    {
        string? lastStage = null;
        int lastCompletedPages = -1;
        return new InlineProgress(value =>
        {
            int step = Math.Max(1, (value.TotalPages + 39) / 40);
            bool stageChanged = !string.Equals(lastStage, value.Stage, StringComparison.Ordinal);
            bool finalPage = value.TotalPages > 0 && value.CompletedPages == value.TotalPages;
            if (!stageChanged && !finalPage && value.CompletedPages - lastCompletedPages < step)
            {
                return;
            }

            lastStage = value.Stage;
            lastCompletedPages = value.CompletedPages;
            report(value);
        });
    }

    private sealed class InlineProgress(Action<PdfInspectionProgress> report) : IProgress<PdfInspectionProgress>
    {
        public void Report(PdfInspectionProgress value) => report(value);
    }
}
