using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Assessments;

public sealed class PdfAssessmentTests
{
    [Fact]
    public void ComponentsAreDerivedSeparatelyFromFindings()
    {
        AssessmentFinding[] findings =
        [
            new("PDF.OPEN.SUCCESS", FindingSeverity.Positive, 50, "Opened", scoreComponentId: PdfAssessment.TechnicalComponentId),
            new("PDF.METADATA.TITLE_PRESENT", FindingSeverity.Positive, 4, "Title", scoreComponentId: PdfAssessment.EmbeddedMetadataComponentId),
            new("PDF.PAGE.UNREADABLE", FindingSeverity.Error, -2, "Page", scoreComponentId: PdfAssessment.TechnicalComponentId),
        ];
        FormatAssessment core = new(
            new CalibreBookId(1), "pdf", "Book/Book.pdf", null, AssessmentStatus.Completed,
            new QualityScore(52), new AnalyzerVersion("pdf-inspector/1.0.0"),
            new ScoringModelVersion("pdf-quality/1.0.0"), findings, PdfAssessment.V1Components,
            new FormatFileObservation(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

        core.Format.Should().Be("PDF");
        core.ScoreComponents.Should().Contain(component => component.Id == PdfAssessment.TechnicalComponentId
            && component.RawContribution == 48 && component.Score == 48);
        core.ScoreComponents.Should().Contain(component => component.Id == PdfAssessment.EmbeddedMetadataComponentId
            && component.RawContribution == 4 && component.Score == 4);
    }

    [Fact]
    public void NonzeroFindingMustBelongToADeclaredComponent()
    {
        AssessmentFinding finding = new("PDF.TEST", FindingSeverity.Positive, 1, "Test", scoreComponentId: new("unknown"));

        Action act = () => _ = new FormatAssessment(
            new CalibreBookId(1), "PDF", "Book.pdf", null, AssessmentStatus.Completed, new QualityScore(1),
            new AnalyzerVersion("pdf-inspector/1.0.0"), new ScoringModelVersion("pdf-quality/1.0.0"),
            [finding], PdfAssessment.V1Components);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SamplingAndIdentifierValuesAreBoundedAndOrdered()
    {
        PdfSamplingSummary sampling = new(
            PdfSamplingMode.DeterministicSample,
            1_000,
            [1, 1000, 500, 1],
            3,
            new PdfPolicyVersion("pdf-sampling/1.0.0"));
        PdfIdentifierEvidence identifier = new("9780306406157", PdfIdentifierSource.EarlyPageText, 1);

        sampling.SelectedPages.Should().Equal(1, 500, 1000);
        sampling.InspectedAllPages.Should().BeFalse();
        identifier.PageNumber.Should().Be(1);
        FluentActions.Invoking(() => new PdfIdentifierEvidence("9780306406158", PdfIdentifierSource.DocumentInformation))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PdfIdentifierEvidence("4006381333931", PdfIdentifierSource.DocumentInformation))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PdfIdentifierEvidence("9780306406157", PdfIdentifierSource.DocumentInformation, 1))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PdfAggregateValuesRejectRawContentLikeCountsBeyondV1Bounds()
    {
        Action text = () => new PdfTextSummary(
            PdfTextExtractionStatus.Available, 200, 200, 200, 10_000_001, 10_000,
            PdfTextDensityBand.Useful, false).Validate();
        Action images = () => new PdfImageSummary(
            200, 200, 200, 20_001, 10_000, PdfContentBalance.ImageHeavy, false).Validate();
        Action active = () => new PdfActiveContentSummary(10_001, 0, 0).Validate();
        Action pages = () => _ = new PdfSamplingSummary(
            PdfSamplingMode.DeterministicSample, 100_001, [1], 1,
            new PdfPolicyVersion("pdf-sampling/1.0.0"));

        text.Should().Throw<ArgumentException>();
        images.Should().Throw<ArgumentException>();
        active.Should().Throw<ArgumentException>();
        pages.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LibrarySnapshotOrdersAndRejectsDuplicatePdfAssociationsIndependently()
    {
        PdfAssessment first = FailedAssessment(1, "A.pdf");
        PdfAssessment second = FailedAssessment(2, "B.pdf");
        LibrarySnapshot snapshot = new(
            new LibraryIdentity("uuid", 27, "library"), DateTimeOffset.UnixEpoch, [], [],
            pdfAssessments: [second, first]);

        snapshot.PdfAssessments.Should().Equal(first, second);
        Action duplicate = () => _ = new LibrarySnapshot(
            new LibraryIdentity("uuid", 27, "library"), DateTimeOffset.UnixEpoch, [], [],
            pdfAssessments: [first, first]);
        duplicate.Should().Throw<ArgumentException>();
    }

    private static PdfAssessment FailedAssessment(long id, string path)
    {
        FormatAssessment result = new(
            new(id), "PDF", path, null, AssessmentStatus.Disqualified, null,
            new("pdf-inspector/1.0.0"), new("pdf-quality/1.0.0"),
            [new AssessmentFinding("PDF.FILE.MISSING", FindingSeverity.Disqualifying, 0, "Missing")],
            PdfAssessment.V1Components);
        PdfFeatureSummary features = new(
            PdfOpenStatus.Unreadable,
            PdfEncryptionStatus.Unknown,
            null,
            null,
            PdfDocumentMetadataSummary.Empty,
            new PdfTextSummary(PdfTextExtractionStatus.Unknown, 0, 0, 0, 0, 0, PdfTextDensityBand.None, false),
            new PdfImageSummary(0, 0, 0, 0, 0, PdfContentBalance.Unknown, false),
            false,
            0,
            new PdfActiveContentSummary(0, 0, 0),
            new PdfPageAnalysisSummary(0, 0, 0, 0, 0, 0),
            [],
            [],
            PdfDocumentClassification.Unreadable,
            PdfClassificationConfidence.Insufficient,
            null,
            new("pdf-classification/1.0.0"),
            new("pdf-limits/1.0.0"),
            true);
        return new(result, features);
    }
}
