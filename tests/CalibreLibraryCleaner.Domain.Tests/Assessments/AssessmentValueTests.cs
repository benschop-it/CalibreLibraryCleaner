using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Assessments;

public sealed class AssessmentValueTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void QualityScoreRejectsValuesOutsideInclusiveRange(int value) =>
        FluentActions.Invoking(() => new QualityScore(value)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void CompletedAssessmentRequiresScoreDerivedFromFindings()
    {
        AssessmentFinding finding = new("EPUB.SCORE.BASELINE", FindingSeverity.Positive, 50, "Baseline");

        FluentActions.Invoking(() => new FormatAssessment(
                new CalibreBookId(1), "EPUB", "Author/Book.epub", null, AssessmentStatus.Completed,
                new QualityScore(51), new AnalyzerVersion("epub-inspector/1.0.0"),
                new ScoringModelVersion("epub-quality/1.0.0"), new EpubFeatureSummary(true, true), [finding]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DisqualifiedAssessmentHasNoNumericScore()
    {
        FormatAssessment assessment = new(
            new CalibreBookId(1), "epub", "Author/Book.epub", null, AssessmentStatus.Disqualified, null,
            new AnalyzerVersion("epub-inspector/1.0.0"), new ScoringModelVersion("epub-quality/1.0.0"),
            new EpubFeatureSummary(false, false),
            [new AssessmentFinding("EPUB.OPEN", FindingSeverity.Disqualifying, 0, "Cannot open")]);

        assessment.Format.Should().Be("EPUB");
        assessment.Score.Should().BeNull();
    }

    [Theory]
    [InlineData("/absolute.epub")]
    [InlineData("../escape.epub")]
    [InlineData("C:/drive.epub")]
    public void AssessmentRejectsUnsafePresentationPath(string path)
    {
        Func<FormatAssessment> act = () => new(
            new CalibreBookId(1), "EPUB", path, null, AssessmentStatus.Disqualified, null,
            new AnalyzerVersion("epub-inspector/1.0.0"), new ScoringModelVersion("epub-quality/1.0.0"),
            new EpubFeatureSummary(false, false),
            [new AssessmentFinding("EPUB.OPEN", FindingSeverity.Disqualifying, 0, "Cannot open")]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AssessmentCopiesAndDeterministicallyOrdersFindingsAndEvidence()
    {
        Dictionary<string, string> mutableEvidence = new(StringComparer.Ordinal) { ["z"] = "last", ["a"] = "first" };
        List<AssessmentFinding> mutableFindings =
        [
            new("EPUB.WARNING", FindingSeverity.Warning, -1, "Warning", mutableEvidence),
            new("EPUB.POSITIVE", FindingSeverity.Positive, 51, "Positive"),
        ];
        FormatAssessment assessment = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Completed, new QualityScore(50),
            new AnalyzerVersion("epub-inspector/1.0.0"), new ScoringModelVersion("epub-quality/1.0.0"),
            new EpubFeatureSummary(true, true), mutableFindings);

        mutableEvidence["new"] = "mutation";
        mutableFindings.Clear();

        assessment.Findings.Select(finding => finding.Severity).Should().Equal(FindingSeverity.Warning, FindingSeverity.Positive);
        assessment.Findings[0].Evidence.Should().NotContainKey("new");
        assessment.Findings[0].Evidence.Keys.Should().Equal("z", "a");
    }

    [Fact]
    public void SnapshotRejectsDuplicateAssessmentAssociations()
    {
        FormatAssessment assessment = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Disqualified, null,
            new AnalyzerVersion("epub-inspector/1.0.0"), new ScoringModelVersion("epub-quality/1.0.0"),
            new EpubFeatureSummary(false, false),
            [new AssessmentFinding("EPUB.OPEN", FindingSeverity.Disqualifying, 0, "Cannot open")]);

        Action act = () => _ = new LibrarySnapshot(
            new LibraryIdentity("uuid", 27, "library"), DateTimeOffset.UnixEpoch, [], [], epubAssessments: [assessment, assessment]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EvidenceBoundsAndAssessmentOnlySeverityAreEnforced()
    {
        Dictionary<string, string> tooMuchEvidence = Enumerable.Range(0, AssessmentFinding.MaximumEvidenceItems + 1)
            .ToDictionary(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "value", StringComparer.Ordinal);

        FluentActions.Invoking(() => new AssessmentFinding("EPUB.TEST", FindingSeverity.Information, 0, "Test", tooMuchEvidence))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new LibraryFinding("TEST", FindingSeverity.Positive, "Message", "Action"))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(600, 0)]
    [InlineData(600, null)]
    public void FeatureSummaryRejectsInvalidCoverDimensionPairs(int? width, int? height) =>
        FluentActions.Invoking(() => new EpubFeatureSummary(true, true, coverPresent: true, coverWidth: width, coverHeight: height))
            .Should().Throw<ArgumentException>();
}
