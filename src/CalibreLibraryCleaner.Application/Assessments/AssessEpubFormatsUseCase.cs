using System.Diagnostics;
using System.Globalization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Assessments;

public sealed partial class AssessEpubFormatsUseCase(
    IEpubInspector inspector,
    EpubAssessmentEngine engine,
    ILogger<AssessEpubFormatsUseCase>? logger = null)
{
    private readonly ILogger<AssessEpubFormatsUseCase> _logger = logger
        ?? NullLogger<AssessEpubFormatsUseCase>.Instance;

    public async Task<IReadOnlyList<EpubAssessment>> ExecuteAsync(
        IReadOnlyList<EpubAssessmentTarget> allTargets,
        int maxConcurrency,
        EpubInspectionLimits limits,
        IProgress<EpubAssessmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allTargets);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ValidateLimits(limits);
        cancellationToken.ThrowIfCancellationRequested();
        EpubAssessmentTarget[] targets = allTargets
            .Where(target => string.Equals(target.Format, "EPUB", StringComparison.OrdinalIgnoreCase))
            .OrderBy(target => target.BookId.Value)
            .ThenBy(target => target.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        EpubAssessment?[] results = new EpubAssessment?[targets.Length];
        EpubProgressCoordinator progressCoordinator = new(progress, targets.Length);

        ParallelOptions options = new() { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, targets.Length), options, async (index, token) =>
        {
            EpubAssessmentTarget target = targets[index];
            progressCoordinator.Start(index, SafePath(target));
            long started = Stopwatch.GetTimestamp();
            LogAssessmentStarted(_logger, target.BookId.Value, target.Fingerprint?.SizeInBytes ?? 0,
                index + 1, targets.Length);
            EpubInspectionResult inspection;
            if (target.FileStatus != FormatFileStatus.Present)
            {
                (EpubInspectionProblemCode code, string explanation) = target.FileStatus switch
                {
                    FormatFileStatus.Missing => (EpubInspectionProblemCode.CannotOpen, "The EPUB file is missing."),
                    FormatFileStatus.InvalidPath => (EpubInspectionProblemCode.UnsafeArchive, "The Calibre-managed EPUB path is invalid."),
                    FormatFileStatus.Inaccessible => (EpubInspectionProblemCode.Unreadable, "The EPUB file is inaccessible or unreadable."),
                    FormatFileStatus.ChangedDuringHashing => (EpubInspectionProblemCode.ChangedDuringInspection, "The EPUB changed during analysis."),
                    _ => (EpubInspectionProblemCode.Unreadable, "The EPUB has no verified readable file identity."),
                };
                inspection = EpubInspectionResult.Failed(target.BookId, SafePath(target), code, explanation);
            }
            else
            {
                if (target.Fingerprint is null
                    || target.Observation is null
                    || string.IsNullOrWhiteSpace(target.LibraryRoot)
                    || string.IsNullOrWhiteSpace(target.FullPath)
                    || target.Fingerprint.SizeInBytes != target.Observation.Length)
                {
                    throw new InvalidOperationException("A present EPUB target requires one consistent verified file identity.");
                }

                EpubInspectionRequest request = new(
                    target.BookId,
                    target.LibraryRoot,
                    target.FullPath,
                    SafePath(target),
                    target.Fingerprint,
                    target.Observation,
                    limits);
                IProgress<EpubInspectionProgress>? inspectionProgress = progress is null
                    ? new InlineInspectionProgress(value => LogAssessmentStage(
                        _logger,
                        target.BookId.Value,
                        value.Stage,
                        value.CompletedUnits,
                        value.TotalUnits ?? 0,
                        ElapsedMilliseconds(started, Stopwatch.GetTimestamp())))
                    : new InlineInspectionProgress(value =>
                    {
                        LogAssessmentStage(
                            _logger,
                            target.BookId.Value,
                            value.Stage,
                            value.CompletedUnits,
                            value.TotalUnits ?? 0,
                            ElapsedMilliseconds(started, Stopwatch.GetTimestamp()));
                        progressCoordinator.ReportStage(index, SafePath(target), FormatInspectionStage(value));
                    });
                try
                {
                    inspection = await inspector.InspectAsync(request, inspectionProgress, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new EpubTargetAssessmentException(SafePath(target), exception);
                }
            }

            token.ThrowIfCancellationRequested();
            results[index] = engine.Assess(target.BookId, SafePath(target), target.Fingerprint, inspection, token);
            long elapsedMilliseconds = ElapsedMilliseconds(started, Stopwatch.GetTimestamp());
            LogAssessmentCompleted(
                _logger,
                target.BookId.Value,
                inspection.Coverage.ToString(),
                inspection.Problems.Count > 0 ? inspection.Problems[0].Code.ToString() : "None",
                inspection.ManifestItemCount,
                inspection.SpineItemCount,
                inspection.ChapterCount,
                inspection.ReadableCharacterCount,
                elapsedMilliseconds);
            if (elapsedMilliseconds >= 5_000)
                LogSlowAssessment(
                    _logger,
                    target.BookId.Value,
                    target.Fingerprint?.SizeInBytes ?? 0,
                    inspection.Coverage.ToString(),
                    elapsedMilliseconds);
            progressCoordinator.Complete(index, SafePath(target));
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return results.Select(result => result ?? throw new InvalidOperationException("An EPUB assessment result is missing.")).ToArray();
    }

    private static string SafePath(EpubAssessmentTarget target) => string.IsNullOrWhiteSpace(target.ExpectedRelativePath)
        ? $"invalid-path/book-{target.BookId.Value}.epub"
        : target.ExpectedRelativePath.Replace('\\', '/');

    private static string FormatInspectionStage(EpubInspectionProgress progress) => progress.TotalUnits is > 0
        ? $"{progress.Stage} {progress.CompletedUnits:N0} of {progress.TotalUnits.Value:N0}"
        : progress.Stage;

    private static long ElapsedMilliseconds(long started, long completed) =>
        (long)Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;

    private static void ValidateLimits(EpubInspectionLimits limits)
    {
        long[] longLimits =
        [
            limits.MaximumFileBytes,
            limits.MaximumDeclaredUncompressedBytes,
            limits.MaximumEntryBytes,
            limits.MaximumXmlBytes,
            limits.MaximumChapterBytes,
            limits.MaximumCssBytes,
            limits.MaximumCoverBytes,
        ];
        int[] integerLimits =
        [
            limits.MaximumArchiveEntries,
            limits.MaximumCoverHeaderBytes,
            limits.MaximumSpineItems,
            limits.MaximumLocalReferences,
            limits.MaximumEvidencePerRule,
            limits.MaximumReadableCharacters,
            limits.MaximumCompressionRatio,
            limits.MaximumAggregateCompressionRatio,
            limits.MaximumHtmlCharacters,
            limits.MaximumHtmlNodes,
            limits.MaximumHtmlDepth,
        ];
        if (longLimits.Any(value => value <= 0) || integerLimits.Any(value => value <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "EPUB inspection limits must all be positive.");
        }

        if (limits.MaximumEvidencePerRule > AssessmentFinding.MaximumEvidenceItems)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "EPUB evidence retention cannot exceed the Domain presentation bound.");
        }
    }

    private sealed class InlineInspectionProgress(Action<EpubInspectionProgress> report) : IProgress<EpubInspectionProgress>
    {
        public void Report(EpubInspectionProgress value) => report(value);
    }

    private sealed class EpubProgressCoordinator
    {
        private static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromMilliseconds(500);
        private readonly IProgress<EpubAssessmentProgress>? _progress;
        private readonly int _totalFiles;
        private readonly object _gate = new();
        private readonly Dictionary<int, string> _activeStages = [];
        private int _completedFiles;
        private long _lastReport = Stopwatch.GetTimestamp();
        private string _lastSummary = string.Empty;

        public EpubProgressCoordinator(IProgress<EpubAssessmentProgress>? progress, int totalFiles)
        {
            _progress = progress;
            _totalFiles = totalFiles;
            _progress?.Report(new(0, totalFiles, string.Empty, "Starting"));
        }

        public void Start(int index, string relativePath)
        {
            lock (_gate)
            {
                _activeStages[index] = "Starting";
                Report(relativePath, "Starting", force: _activeStages.Count == 1);
            }
        }

        public void ReportStage(int index, string relativePath, string stage)
        {
            lock (_gate)
            {
                _activeStages[index] = stage;
                Report(relativePath, stage, force: _activeStages.Count == 1);
            }
        }

        public void Complete(int index, string relativePath)
        {
            lock (_gate)
            {
                _activeStages.Remove(index);
                _completedFiles++;
                Report(relativePath, "Complete", force: true);
            }
        }

        private void Report(string relativePath, string stage, bool force)
        {
            if (_progress is null) return;
            long now = Stopwatch.GetTimestamp();
            string summary = string.Join(", ", _activeStages.Values
                .Select(BaseStage)
                .GroupBy(value => value, StringComparer.Ordinal)
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}"));
            bool summaryChanged = !string.Equals(summary, _lastSummary, StringComparison.Ordinal);
            if (!force && !summaryChanged
                && Stopwatch.GetElapsedTime(_lastReport, now) < MinimumUpdateInterval)
                return;
            _lastReport = now;
            _lastSummary = summary;
            _progress.Report(new(
                _completedFiles,
                _totalFiles,
                _activeStages.Count <= 1 ? relativePath : string.Empty,
                stage,
                _activeStages.Count,
                summary));
        }

        private static string BaseStage(string stage)
        {
            int separator = stage.IndexOf(' ');
            return separator < 0 ? stage : stage[..separator];
        }
    }

    [LoggerMessage(300, LogLevel.Debug,
        "EPUB assessment started. RecordId={RecordId}, FileBytes={FileBytes}, FileIndex={FileIndex}, TotalFiles={TotalFiles}.")]
    private static partial void LogAssessmentStarted(
        ILogger logger,
        long recordId,
        long fileBytes,
        int fileIndex,
        int totalFiles);

    [LoggerMessage(301, LogLevel.Debug,
        "EPUB assessment stage. RecordId={RecordId}, Stage={Stage}, CompletedUnits={CompletedUnits}, TotalUnits={TotalUnits}, ElapsedMilliseconds={ElapsedMilliseconds}.")]
    private static partial void LogAssessmentStage(
        ILogger logger,
        long recordId,
        string stage,
        int completedUnits,
        int totalUnits,
        long elapsedMilliseconds);

    [LoggerMessage(302, LogLevel.Debug,
        "EPUB assessment completed. RecordId={RecordId}, Coverage={Coverage}, ProblemCode={ProblemCode}, ManifestItems={ManifestItems}, SpineItems={SpineItems}, Chapters={Chapters}, ReadableCharacters={ReadableCharacters}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogAssessmentCompleted(
        ILogger logger,
        long recordId,
        string coverage,
        string problemCode,
        int manifestItems,
        int spineItems,
        int chapters,
        int readableCharacters,
        long totalMilliseconds);

    [LoggerMessage(303, LogLevel.Warning,
        "Slow EPUB assessment detected. RecordId={RecordId}, FileBytes={FileBytes}, Coverage={Coverage}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogSlowAssessment(
        ILogger logger,
        long recordId,
        long fileBytes,
        string coverage,
        long totalMilliseconds);
}

internal sealed class EpubTargetAssessmentException(string relativePath, Exception innerException)
    : Exception("An EPUB inspector failed unexpectedly.", innerException)
{
    public string RelativePath { get; } = relativePath;

    public string FailureType { get; } = innerException.GetType().Name;
}
