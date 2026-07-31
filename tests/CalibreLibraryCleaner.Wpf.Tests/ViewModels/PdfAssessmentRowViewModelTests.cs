using System.Reflection;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class PdfAssessmentRowViewModelTests
{
    [Fact]
    public void RowDisplaysClassificationSamplingVersionsComponentsAndLazyFindings()
    {
        PdfInspectionResult inspection = new(
            new(1),
            "Book/Book.pdf",
            PdfOpenStatus.Opened,
            PdfEncryptionStatus.NotEncrypted,
            250,
            "1.7",
            new(
                new(PdfMetadataValueStatus.Present, "Synthetic title"),
                new(PdfMetadataValueStatus.Present, "Synthetic author"),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Missing)),
            true,
            3,
            new(0, 0, 0),
            [1, 2, 3, 248, 249, 250],
            [
                new(1, true, 500, 500, 0, 0, 0, 20, 595000, 842000, null, "a", true, null, null, false, false),
                new(2, true, 500, 500, 0, 0, 0, 20, 595000, 842000, null, "b", true, null, null, false, false),
                new(3, true, 500, 500, 0, 0, 0, 20, 595000, 842000, null, "c", true, null, null, false, false),
                new(248, true, 500, 500, 0, 0, 0, 20, 595000, 842000, null, "d", true, null, null, false, false),
                new(249, true, 500, 500, 0, 0, 0, 20, 595000, 842000, null, "e", true, null, null, false, false),
                new(250, true, 500, 500, 0, 0, 0, 20, 595000, 842000, null, "f", true, null, null, false, false),
            ],
            [new("9780306406157", PdfIdentifierSource.DocumentInformation)],
            0,
            20,
            100,
            true,
            false,
            []);
        PdfAssessment assessment = new PdfAssessmentEngine(new PdfClassificationPolicy()).Assess(
            new(1),
            "Book/Book.pdf",
            new(100, new(new string('a', 64))),
            inspection,
            new(100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

        PdfAssessmentRowViewModel row = new(assessment, null);
        FieldInfo field = typeof(PdfAssessmentRowViewModel).GetField("findings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Lazy<IReadOnlyList<PdfAssessmentFindingRowViewModel>> lazy =
            field.GetValue(row).Should().BeOfType<Lazy<IReadOnlyList<PdfAssessmentFindingRowViewModel>>>().Subject;

        row.Classification.Should().Be("DigitalText");
        row.Confidence.Should().Be("Medium");
        row.Sampling.Should().Be("Sampled 6 of 250 pages");
        row.Encryption.Should().Be("NotEncrypted");
        row.AnalyzerVersion.Should().Be("pdf-inspector/1.0.0");
        row.ScoringModelVersion.Should().Be("pdf-quality/1.0.0");
        row.FeatureSummary.Should().Contain("Technical score:")
            .And.Contain("Classification is independent of score")
            .And.Contain("sampled evidence does not establish whole-document certainty")
            .And.Contain("glyphs may be hidden, scrambled, or semantically incorrect")
            .And.Contain("No OCR was run")
            .And.Contain("pdf-classification/1.0.0")
            .And.Contain("pdf-limits/1.0.0");
        lazy.IsValueCreated.Should().BeFalse();
        row.Findings.Should().NotBeEmpty().And.OnlyContain(finding => !string.IsNullOrWhiteSpace(finding.Component));
        lazy.IsValueCreated.Should().BeTrue();
    }

    [Fact]
    public void DisqualifiedRowExplicitlyDisplaysNoScoreAndEncryptionReason()
    {
        PdfInspectionResult inspection = PdfInspectionResult.Failed(
            new(1), "Book.pdf", PdfInspectionProblemCode.PasswordRequired,
            PdfOpenStatus.Unreadable, PdfEncryptionStatus.PasswordRequired);
        PdfAssessment assessment = new PdfAssessmentEngine(new PdfClassificationPolicy()).Assess(
            new(1), "Book.pdf", null, inspection);

        PdfAssessmentRowViewModel row = new(assessment, null);

        row.Status.Should().Be("Disqualified");
        row.Score.Should().Contain("Not scored").And.Contain("disqualified");
        row.Classification.Should().Be("Encrypted");
        row.Encryption.Should().Be("PasswordRequired");
        row.FeatureSummary.Should().Contain("Technical score: not scored; metadata score: not scored")
            .And.NotContain("Technical score: 0/85")
            .And.Contain("No OCR was run");
        row.Findings.Should().Contain(finding => finding.Severity == "Disqualifying");
    }
}
