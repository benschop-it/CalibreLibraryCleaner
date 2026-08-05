using System.Reflection;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class EpubAssessmentRowViewModelTests
{
    [Fact]
    public void FindingRowsAreMaterializedOnlyWhenRequested()
    {
        AssessmentFinding finding = new("EPUB.BASELINE", FindingSeverity.Information, 50, "Baseline.");
        EpubAssessment assessment = new(
            new CalibreBookId(1),
            "EPUB",
            "Book/Book.epub",
            null,
            AssessmentStatus.Completed,
            new QualityScore(50),
            new AnalyzerVersion("epub-inspector/1.0.3"),
            new ScoringModelVersion("epub-quality/1.0.2"),
            new EpubFeatureSummary(true, true),
            [finding]);
        EpubAssessmentRowViewModel row = new(assessment, null);
        FieldInfo field = typeof(EpubAssessmentRowViewModel).GetField("_findings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Lazy<IReadOnlyList<EpubAssessmentFindingRowViewModel>> lazyFindings =
            field.GetValue(row).Should().BeOfType<Lazy<IReadOnlyList<EpubAssessmentFindingRowViewModel>>>().Subject;

        lazyFindings.IsValueCreated.Should().BeFalse();
        _ = row.FeatureSummary;
        lazyFindings.IsValueCreated.Should().BeFalse();

        row.Findings.Should().ContainSingle();
        lazyFindings.IsValueCreated.Should().BeTrue();
    }

    [Fact]
    public void UnassessedRowDoesNotCallTheBookDisqualified()
    {
        AssessmentFinding finding = new("EPUB.PACKAGE", FindingSeverity.Warning, 0, "Package could not be safely assessed.");
        EpubAssessment assessment = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Unassessed, null,
            new AnalyzerVersion("epub-inspector/1.0.3"), new ScoringModelVersion("epub-quality/1.0.2"),
            new EpubFeatureSummary(false, false), [finding]);

        EpubAssessmentRowViewModel row = new(assessment, null);

        row.Status.Should().Be("Unassessed");
        row.Score.Should().Be("Not scored — unassessed");
        row.ScoreSortValue.Should().BeNull();
    }

    [Fact]
    public void FallbackReadableRowShowsNumericScoreCoverageAndCap()
    {
        AssessmentFinding finding = new("EPUB.BASELINE", FindingSeverity.Information, 89, "Fallback score evidence.");
        EpubAssessment assessment = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Completed, new QualityScore(70),
            new AnalyzerVersion("epub-inspector/1.0.4"), new ScoringModelVersion("epub-quality/1.0.3"),
            new EpubFeatureSummary(
                true,
                false,
                readableCharacterCount: 6_000,
                coverage: EpubAssessmentCoverage.FallbackReadable,
                availableFacets: EpubAssessmentFacet.Archive | EpubAssessmentFacet.Content,
                fallbackCandidateCount: 1,
                fallbackRenderableCount: 1,
                renderableEvidence: EpubRenderableEvidence.Text),
            [finding],
            scoreCap: 70);

        EpubAssessmentRowViewModel row = new(assessment, null);

        row.Status.Should().Be("Completed");
        row.Coverage.Should().Be("FallbackReadable");
        row.Score.Should().Be("70 (fallback; max 70)");
        row.ScoreSortValue.Should().Be(70);
        row.FeatureSummary.Should().Contain("Uncapped score: 89").And.Contain("Renderable evidence: Text");
    }
}
