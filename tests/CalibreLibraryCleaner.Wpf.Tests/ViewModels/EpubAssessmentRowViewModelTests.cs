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
    }
}
