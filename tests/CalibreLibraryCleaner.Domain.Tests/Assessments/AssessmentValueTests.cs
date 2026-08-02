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

        FluentActions.Invoking(() => new EpubAssessment(
                new CalibreBookId(1), "EPUB", "Author/Book.epub", null, AssessmentStatus.Completed,
                new QualityScore(51), new AnalyzerVersion("epub-inspector/1.0.0"),
                new ScoringModelVersion("epub-quality/1.0.0"), new EpubFeatureSummary(true, true), [finding]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CompletedAssessmentAppliesExplicitScoreCeilingAfterFindingDerivation()
    {
        AssessmentFinding finding = new("EPUB.SCORE.BASELINE", FindingSeverity.Positive, 80, "Synthetic raw score.");

        FormatAssessment assessment = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Completed,
            new QualityScore(70), new AnalyzerVersion("epub-inspector/1.0.4"),
            new ScoringModelVersion("epub-quality/1.0.3"), [finding], scoreCap: 70);

        assessment.Score.Should().Be(new QualityScore(70));
        assessment.UncappedScore.Should().Be(new QualityScore(80));
        assessment.ScoreCap.Should().Be(70);
    }

    [Fact]
    public void ScoreCeilingRejectsInconsistentOrUnscoredAssessments()
    {
        AssessmentFinding positive = new("EPUB.SCORE.BASELINE", FindingSeverity.Positive, 80, "Synthetic raw score.");
        AssessmentFinding warning = new("EPUB.PACKAGE", FindingSeverity.Warning, 0, "Incomplete.");

        FluentActions.Invoking(() => new FormatAssessment(
                new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Completed,
                new QualityScore(80), new AnalyzerVersion("epub-inspector/1.0.4"),
                new ScoringModelVersion("epub-quality/1.0.3"), [positive], scoreCap: 70))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new FormatAssessment(
                new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Unassessed,
                null, new AnalyzerVersion("epub-inspector/1.0.4"),
                new ScoringModelVersion("epub-quality/1.0.3"), [warning], scoreCap: 70))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FallbackFeatureSummaryRequiresRenderableEvidenceAndContentFacet()
    {
        EpubFeatureSummary summary = new(
            true,
            false,
            readableCharacterCount: 250,
            coverage: EpubAssessmentCoverage.FallbackReadable,
            availableFacets: EpubAssessmentFacet.Archive | EpubAssessmentFacet.Content,
            fallbackCandidateCount: 3,
            fallbackRenderableCount: 1,
            renderableEvidence: EpubRenderableEvidence.Text);

        summary.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        summary.AvailableFacets.Should().HaveFlag(EpubAssessmentFacet.Content);
        summary.FallbackCandidateCount.Should().Be(3);
        summary.FallbackRenderableCount.Should().Be(1);
        summary.RenderableEvidence.Should().Be(EpubRenderableEvidence.Text);

        FluentActions.Invoking(() => new EpubFeatureSummary(
                true,
                false,
                coverage: EpubAssessmentCoverage.FallbackReadable,
                availableFacets: EpubAssessmentFacet.Archive,
                fallbackCandidateCount: 1,
                fallbackRenderableCount: 0,
                renderableEvidence: EpubRenderableEvidence.None))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EpubAssessmentRequiresFallbackCoverageAndScoreCapToAgree()
    {
        AssessmentFinding finding = new("EPUB.SCORE.BASELINE", FindingSeverity.Positive, 80, "Synthetic raw score.");
        FormatAssessment uncapped = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Completed,
            new QualityScore(80), new AnalyzerVersion("epub-inspector/1.0.4"),
            new ScoringModelVersion("epub-quality/1.0.3"), [finding]);
        EpubFeatureSummary fallback = new(
            true,
            false,
            coverage: EpubAssessmentCoverage.FallbackReadable,
            availableFacets: EpubAssessmentFacet.Archive | EpubAssessmentFacet.Content,
            fallbackCandidateCount: 1,
            fallbackRenderableCount: 1,
            renderableEvidence: EpubRenderableEvidence.Text);
        FormatAssessment capped = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Completed,
            new QualityScore(70), new AnalyzerVersion("epub-inspector/1.0.4"),
            new ScoringModelVersion("epub-quality/1.0.3"), [finding], scoreCap: 70);

        FluentActions.Invoking(() => new EpubAssessment(uncapped, fallback)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new EpubAssessment(capped, new EpubFeatureSummary(true, true))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DisqualifiedAssessmentHasNoNumericScore()
    {
        EpubAssessment assessment = new(
            new CalibreBookId(1), "epub", "Author/Book.epub", null, AssessmentStatus.Disqualified, null,
            new AnalyzerVersion("epub-inspector/1.0.0"), new ScoringModelVersion("epub-quality/1.0.0"),
            new EpubFeatureSummary(false, false),
            [new AssessmentFinding("EPUB.OPEN", FindingSeverity.Disqualifying, 0, "Cannot open")]);

        assessment.Format.Should().Be("EPUB");
        assessment.Score.Should().BeNull();
    }

    [Fact]
    public void UnassessedAssessmentHasNoScoreOrDisqualifyingFinding()
    {
        AssessmentFinding finding = new("EPUB.PACKAGE", FindingSeverity.Warning, 0, "Package could not be safely assessed.");

        FormatAssessment assessment = new(
            new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Unassessed, null,
            new AnalyzerVersion("epub-inspector/1.0.3"), new ScoringModelVersion("epub-quality/1.0.2"), [finding]);

        assessment.Status.Should().Be(AssessmentStatus.Unassessed);
        assessment.Score.Should().BeNull();
        assessment.Findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);

        FluentActions.Invoking(() => new FormatAssessment(
                new CalibreBookId(1), "EPUB", "Book.epub", null, AssessmentStatus.Unassessed, null,
                new AnalyzerVersion("epub-inspector/1.0.3"), new ScoringModelVersion("epub-quality/1.0.2"),
                [new AssessmentFinding("EPUB.PACKAGE", FindingSeverity.Warning, -1, "Hidden penalty.")]))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("/absolute.epub")]
    [InlineData("../escape.epub")]
    [InlineData("C:/drive.epub")]
    public void AssessmentRejectsUnsafePresentationPath(string path)
    {
        Func<EpubAssessment> act = () => new(
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
        EpubAssessment assessment = new(
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
        EpubAssessment assessment = new(
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
