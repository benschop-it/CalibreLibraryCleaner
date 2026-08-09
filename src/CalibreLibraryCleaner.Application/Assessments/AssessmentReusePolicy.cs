using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Assessments;

public sealed record EpubAssessmentReuseResult(
    IReadOnlyList<EpubAssessment> Reused,
    IReadOnlyList<EpubAssessmentTarget> FreshTargets);

public sealed record PdfAssessmentReuseResult(
    IReadOnlyList<PdfAssessment> Reused,
    IReadOnlyList<PdfAssessmentTarget> FreshTargets);

public static class AssessmentReusePolicy
{
    public static EpubAssessmentReuseResult PartitionEpub(
        LibrarySnapshot? previous,
        IEnumerable<EpubAssessmentTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        EpubAssessmentTarget[] ordered = targets.OrderBy(value => value.BookId.Value)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal).ToArray();
        Dictionary<FormatFileFingerprint, EpubAssessment> reusable = previous?.EpubAssessments
            .Where(value => value.ObservedFingerprint is not null
                && value.AnalyzerVersion == EpubAssessmentEngine.AnalyzerVersion
                && value.ScoringModelVersion == EpubAssessmentEngine.ScoringModelVersion)
            .GroupBy(value => value.ObservedFingerprint!)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(value => value.CalibreBookId.Value)
                .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal).First()) ?? [];
        List<EpubAssessment> reused = [];
        List<EpubAssessmentTarget> fresh = [];
        foreach (EpubAssessmentTarget target in ordered)
        {
            if (target.FileStatus != FormatFileStatus.Present
                || target.Fingerprint is null
                || !reusable.TryGetValue(target.Fingerprint, out EpubAssessment? source))
            {
                fresh.Add(target);
                continue;
            }
            try
            {
                reused.Add(new(
                    target.BookId,
                    "EPUB",
                    SafePath(target.ExpectedRelativePath, target.BookId, "epub"),
                    target.Fingerprint,
                    source.Status,
                    source.Score,
                    EpubAssessmentEngine.AnalyzerVersion,
                    EpubAssessmentEngine.ScoringModelVersion,
                    source.Features,
                    source.Findings,
                    source.ScoreCap));
            }
            catch (ArgumentException)
            {
                fresh.Add(target);
            }
        }
        return new(
            new ReadOnlyCollection<EpubAssessment>(reused),
            new ReadOnlyCollection<EpubAssessmentTarget>(fresh));
    }

    public static PdfAssessmentReuseResult PartitionPdf(
        LibrarySnapshot? previous,
        IEnumerable<PdfAssessmentTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        PdfAssessmentTarget[] ordered = targets.OrderBy(value => value.BookId.Value)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal).ToArray();
        Dictionary<FormatFileFingerprint, PdfAssessment> reusable = previous?.PdfAssessments
            .Where(value => value.ObservedFingerprint is not null
                && value.AnalyzerVersion == PdfAssessmentEngine.AnalyzerVersion
                && value.ScoringModelVersion == PdfAssessmentEngine.ScoringModelVersion
                && value.Features.ResourceProfileVersion == PdfAssessmentEngine.ResourceProfileVersion)
            .GroupBy(value => value.ObservedFingerprint!)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(value => value.CalibreBookId.Value)
                .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal).First()) ?? [];
        List<PdfAssessment> reused = [];
        List<PdfAssessmentTarget> fresh = [];
        foreach (PdfAssessmentTarget target in ordered)
        {
            if (target.FileStatus != FormatFileStatus.Present
                || target.Fingerprint is null
                || target.Observation is null
                || !reusable.TryGetValue(target.Fingerprint, out PdfAssessment? source))
            {
                fresh.Add(target);
                continue;
            }
            try
            {
                FormatAssessment result = new(
                    target.BookId,
                    "PDF",
                    SafePath(target.ExpectedRelativePath, target.BookId, "pdf"),
                    target.Fingerprint,
                    source.Status,
                    source.Score,
                    PdfAssessmentEngine.AnalyzerVersion,
                    PdfAssessmentEngine.ScoringModelVersion,
                    source.Findings,
                    PdfAssessment.V1Components,
                    target.Observation,
                    source.Result.ScoreCap);
                reused.Add(new(result, source.Features));
            }
            catch (ArgumentException)
            {
                fresh.Add(target);
            }
        }
        return new(
            new ReadOnlyCollection<PdfAssessment>(reused),
            new ReadOnlyCollection<PdfAssessmentTarget>(fresh));
    }

    private static string SafePath(string path, CalibreBookId bookId, string extension) =>
        string.IsNullOrWhiteSpace(path)
            ? $"invalid-path/book-{bookId.Value}.{extension}"
            : path.Replace('\\', '/');
}
