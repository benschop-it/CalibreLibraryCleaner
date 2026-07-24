using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Assessments;

public sealed class AssessEpubFormatsUseCase(IEpubInspector inspector, EpubAssessmentEngine engine)
{
    public async Task<IReadOnlyList<FormatAssessment>> ExecuteAsync(
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
        FormatAssessment?[] results = new FormatAssessment?[targets.Length];
        int completed = 0;
        object progressGate = new();
        progress?.Report(new(0, targets.Length, string.Empty, "Starting"));

        ParallelOptions options = new() { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, targets.Length), options, async (index, token) =>
        {
            EpubAssessmentTarget target = targets[index];
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
                    ? null
                    : new InlineInspectionProgress(value => progress.Report(new(
                        Volatile.Read(ref completed),
                        targets.Length,
                        SafePath(target),
                        value.Stage)));
                inspection = await inspector.InspectAsync(request, inspectionProgress, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            results[index] = engine.Assess(target.BookId, SafePath(target), target.Fingerprint, inspection, token);
            lock (progressGate)
            {
                int count = ++completed;
                progress?.Report(new(count, targets.Length, SafePath(target), "Complete"));
            }
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return results.Select(result => result ?? throw new InvalidOperationException("An EPUB assessment result is missing.")).ToArray();
    }

    private static string SafePath(EpubAssessmentTarget target) => string.IsNullOrWhiteSpace(target.ExpectedRelativePath)
        ? $"invalid-path/book-{target.BookId.Value}.epub"
        : target.ExpectedRelativePath.Replace('\\', '/');

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
}
