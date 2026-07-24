using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Assessments;

public sealed class EpubAssessmentEngine
{
    private readonly int _baseline = 50;

    public static AnalyzerVersion AnalyzerVersion { get; } = new("epub-inspector/1.0.1");
    public static ScoringModelVersion ScoringModelVersion { get; } = new("epub-quality/1.0.0");

    public FormatAssessment Assess(
        CalibreBookId bookId,
        string expectedRelativePath,
        FormatFileFingerprint? fingerprint,
        EpubInspectionResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.BookId != bookId || !string.Equals(result.ExpectedRelativePath, expectedRelativePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The EPUB inspector returned a mismatched association.");
        }

        List<AssessmentFinding> findings = [Finding("EPUB.SCORE.BASELINE", FindingSeverity.Positive, _baseline, "Visible scoring baseline.")];
        foreach (EpubInspectionProblem problem in result.Problems.OrderBy(problem => problem.Code).ThenBy(problem => problem.Evidence, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(Finding(ProblemRule(problem.Code), FindingSeverity.Disqualifying, 0, problem.Explanation, problem.Evidence));
        }

        if (result.Problems.Count == 0)
        {
            AddBinary(findings, "EPUB.OPEN", result.Opened, 4, -0, "The EPUB container opened successfully.", "The EPUB container could not be opened.", FindingSeverity.Disqualifying);
            AddBinary(findings, "EPUB.ARCHIVE_SAFETY", result.ArchiveSafe, 0, 0, "Archive safety preflight completed within configured limits.", "Archive safety preflight did not complete successfully.", FindingSeverity.Disqualifying);
            AddBinary(findings, "EPUB.PACKAGE", result.PackageParsed, 4, 0, "The EPUB package parsed successfully.", "The EPUB package could not be parsed.", FindingSeverity.Disqualifying);
            AddBinary(findings, "EPUB.METADATA.TITLE", !string.IsNullOrWhiteSpace(result.EmbeddedTitle), 3, -4, "Embedded title is present.", "Embedded title is missing.");
            AddBinary(findings, "EPUB.METADATA.AUTHOR", result.Authors.Count > 0, 3, -4, "Embedded author metadata is present.", "Embedded author metadata is missing.");
            AddBinary(findings, "EPUB.METADATA.LANGUAGE", result.Languages.Count > 0, 2, -2, "Embedded language metadata is present.", "Embedded language metadata is missing.");
            bool hasDate = result.Dates.Any(value => DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out _));
            findings.Add(hasDate
                ? Finding("EPUB.METADATA.DATE", FindingSeverity.Positive, 1, "A parseable embedded publication date is present.")
                : Finding("EPUB.METADATA.DATE", result.Dates.Count == 0 ? FindingSeverity.Information : FindingSeverity.Warning, result.Dates.Count == 0 ? 0 : -1, result.Dates.Count == 0 ? "No embedded publication date is declared." : "The declared publication date is malformed."));
            AddBinary(findings, "EPUB.METADATA.STRONG_IDENTIFIER", result.StrongIdentifiers.Any(IsValidIsbn), 1, 0, "A valid embedded ISBN is present.", "No valid embedded ISBN is present.", FindingSeverity.Information);
            AddBinary(findings, "EPUB.COVER.PRESENT", result.CoverPresent, 4, -6, "A local cover resource is present.", "A usable local cover resource is missing.");
            bool dimensionsKnown = result.CoverWidth is not null && result.CoverHeight is not null;
            bool usefulCover = dimensionsKnown && Math.Min(result.CoverWidth!.Value, result.CoverHeight!.Value) >= 600 && Math.Max(result.CoverWidth.Value, result.CoverHeight.Value) >= 800;
            findings.Add(!dimensionsKnown
                ? result.CoverHeaderMalformed
                    ? Finding("EPUB.COVER.DIMENSIONS", FindingSeverity.Warning, -2, "The declared cover has a malformed supported image header.")
                    : Finding("EPUB.COVER.DIMENSIONS", FindingSeverity.Information, 0, "Cover dimensions could not be safely determined.")
                : usefulCover
                    ? Finding("EPUB.COVER.DIMENSIONS", FindingSeverity.Positive, 2, "Cover dimensions meet the V1 usefulness threshold.")
                    : Finding("EPUB.COVER.DIMENSIONS", FindingSeverity.Warning, -3, "Cover dimensions are below the V1 usefulness threshold."));
            AddBinary(findings, "EPUB.NAVIGATION", result.NavigationPresent, 4, -6, "A usable navigation document is present.", "A usable navigation document is missing.");
            AddBinary(findings, "EPUB.SPINE.NON_EMPTY", result.SpineItemCount > 0, 5, -20, "The reading spine is non-empty.", "The reading spine is empty.", FindingSeverity.Error);
            AddRepeated(findings, "EPUB.SPINE.RESOURCE_EXISTS", result.MissingSpineResources, result.TotalMissingSpineResources, 4, -5, -20, "All spine resources resolve locally.", "A spine resource is missing.");
            AddRepeated(findings, "EPUB.RESOURCE.INTERNAL_EXISTS", result.BrokenInternalReferences, result.TotalBrokenInternalReferences, 4, -2, -10, "All inspected internal references resolve locally.", "An internal resource reference is broken.");
            foreach (string remoteReference in result.RemoteReferences.Order(StringComparer.Ordinal).Take(100))
            {
                findings.Add(Finding("EPUB.RESOURCE.REMOTE_REFERENCE", FindingSeverity.Information, 0, "An external content reference was recorded but never fetched.", remoteReference));
            }
            AddOmittedEvidence(findings, "EPUB.RESOURCE.REMOTE_REFERENCE", result.RemoteReferences.Count, result.TotalRemoteReferences);
            findings.Add(result.ReadableCharacterCount >= 5_000
                ? Finding("EPUB.TEXT.SUBSTANTIAL", FindingSeverity.Positive, 5, "Substantial readable text is present.")
                : Finding("EPUB.TEXT.SUBSTANTIAL", FindingSeverity.Error, result.ReadableCharacterCount >= 500 ? -8 : -15, result.ReadableCharacterCount >= 500 ? "Readable text is limited." : "Readable text is suspiciously small."));
            AddRepeated(findings, "EPUB.CHAPTER.EMPTY", result.EmptyChapters, result.TotalEmptyChapters, 2, -2, -10, "No content chapter is empty or near-empty.", "A content chapter is empty or near-empty.");
            AddRepeated(findings, "EPUB.STRUCTURE.REPEATED_REFERENCE", result.RepeatedReferences, result.TotalRepeatedReferences, 2, -4, -12, "No repeated chapter/reference structure was detected.", "A repeated chapter/reference target was detected.");
            findings.Add(Finding("EPUB.ENCRYPTION", FindingSeverity.Information, 0, result.EncryptionState == "None" ? "No blocking encryption was detected." : $"Encryption state: {result.EncryptionState}."));
            foreach (string truncation in (result.OptionalTruncations ?? []).Order(StringComparer.Ordinal).Take(100))
            {
                findings.Add(Finding("EPUB.ANALYSIS.TRUNCATED", FindingSeverity.Error, 0, "Optional EPUB analysis stopped at a configured safety limit.", truncation));
            }

            if (result.AnalysisTruncated && (result.OptionalTruncations?.Count ?? 0) == 0)
            {
                findings.Add(Finding("EPUB.ANALYSIS.TRUNCATED", FindingSeverity.Information, 0, "Optional EPUB analysis was truncated within configured safety limits."));
            }
        }

        bool disqualified = findings.Any(finding => finding.Severity == FindingSeverity.Disqualifying);
        QualityScore? score = disqualified ? null : new(Math.Clamp(findings.Sum(finding => finding.ScoreAdjustment), 0, 100));
        EpubFeatureSummary summary = new(
            result.Opened,
            result.PackageParsed,
            result.PackageVersion,
            result.EmbeddedTitle,
            result.Authors,
            result.Languages,
            result.Dates,
            result.StrongIdentifiers,
            result.CoverPresent,
            result.CoverWidth,
            result.CoverHeight,
            result.NavigationPresent,
            result.ManifestItemCount,
            result.SpineItemCount,
            result.ChapterCount,
            result.LocalResourceCount,
            result.TotalBrokenInternalReferences ?? result.BrokenInternalReferences.Count,
            result.ReadableCharacterCount,
            result.EncryptionState,
            result.AnalysisTruncated);
        return new(bookId, "EPUB", expectedRelativePath, fingerprint, disqualified ? AssessmentStatus.Disqualified : AssessmentStatus.Completed, score, AnalyzerVersion, ScoringModelVersion, summary, findings);
    }

    private static void AddBinary(List<AssessmentFinding> findings, string id, bool success, int positive, int negative, string positiveText, string negativeText, FindingSeverity negativeSeverity = FindingSeverity.Warning) =>
        findings.Add(success ? Finding(id, FindingSeverity.Positive, positive, positiveText) : Finding(id, negativeSeverity, negative, negativeText));

    private static void AddRepeated(List<AssessmentFinding> findings, string id, IReadOnlyList<string> evidence, int? totalEvidence, int positive, int penalty, int cap, string positiveText, string negativeText)
    {
        if (evidence.Count == 0)
        {
            findings.Add(Finding(id, FindingSeverity.Positive, positive, positiveText));
            return;
        }

        int applied = 0;
        string[] orderedEvidence = evidence.Order(StringComparer.Ordinal).ToArray();
        foreach (string item in orderedEvidence.Take(100))
        {
            int adjustment = applied <= cap ? 0 : Math.Max(penalty, cap - applied);
            applied += adjustment;
            findings.Add(Finding(id, FindingSeverity.Error, adjustment, adjustment == 0 ? $"{negativeText} The scoring cap was reached." : negativeText, item));
        }

        int retainedCount = Math.Min(orderedEvidence.Length, 100);
        int knownTotal = Math.Max(totalEvidence ?? orderedEvidence.Length, orderedEvidence.Length);
        int omittedCount = knownTotal - retainedCount;
        if (omittedCount > 0)
        {
            int omittedAdjustment = applied <= cap
                ? 0
                : Math.Max(checked(penalty * omittedCount), cap - applied);
            applied += omittedAdjustment;
            findings.Add(Finding(
                id,
                omittedAdjustment == 0 ? FindingSeverity.Information : FindingSeverity.Error,
                omittedAdjustment,
                omittedAdjustment == 0
                    ? "Additional evidence was omitted after the presentation-safe limit; the scoring cap was already reached."
                    : "Additional bounded-out occurrences contributed to the repeated penalty.",
                $"omitted:{omittedCount}"));
        }
    }

    private static void AddOmittedEvidence(List<AssessmentFinding> findings, string id, int retainedCount, int? totalCount)
    {
        if (totalCount is not > 0 || totalCount <= retainedCount)
        {
            return;
        }

        findings.Add(Finding(
            id,
            FindingSeverity.Information,
            0,
            "Additional evidence was omitted after the presentation-safe limit; all occurrences were counted.",
            $"omitted:{totalCount.Value - retainedCount}"));
    }

    private static AssessmentFinding Finding(string id, FindingSeverity severity, int adjustment, string explanation, string? evidence = null) => new(
        id,
        severity,
        adjustment,
        explanation,
        evidence is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["item"] = evidence });

    private static string ProblemRule(EpubInspectionProblemCode code) => code switch
    {
        EpubInspectionProblemCode.CannotOpen or EpubInspectionProblemCode.Unreadable => "EPUB.OPEN",
        EpubInspectionProblemCode.UnsafeArchive or EpubInspectionProblemCode.LimitExceeded => "EPUB.ARCHIVE_SAFETY",
        EpubInspectionProblemCode.PackageMalformed => "EPUB.PACKAGE",
        EpubInspectionProblemCode.Encrypted => "EPUB.ENCRYPTION",
        EpubInspectionProblemCode.ChangedDuringInspection => "EPUB.FILE_CHANGED",
        _ => "EPUB.UNSUPPORTED",
    };

    private static bool IsValidIsbn(string value)
    {
        string isbn = new string(value.Where(character => char.IsDigit(character) || character is 'X' or 'x').ToArray()).ToUpperInvariant();
        if (isbn.Length == 10)
        {
            int total = 0;
            for (int index = 0; index < 10; index++) total += (10 - index) * (isbn[index] == 'X' ? 10 : isbn[index] - '0');
            return total % 11 == 0;
        }

        if (isbn.Length == 13 && isbn.All(char.IsDigit))
        {
            int total = 0;
            for (int index = 0; index < 12; index++) total += (isbn[index] - '0') * (index % 2 == 0 ? 1 : 3);
            return (10 - (total % 10)) % 10 == isbn[12] - '0';
        }

        return false;
    }
}
