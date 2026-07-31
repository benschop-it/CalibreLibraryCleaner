using System.Collections.ObjectModel;
using System.Globalization;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class PdfAssessmentRowViewModel
{
    private readonly Lazy<IReadOnlyList<PdfAssessmentFindingRowViewModel>> findings;
    private readonly Lazy<string> featureSummary;

    public PdfAssessmentRowViewModel(PdfAssessment assessment, CalibreBook? book)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        BookId = assessment.CalibreBookId.Value;
        BookTitle = book?.Title ?? string.Empty;
        ExpectedRelativePath = assessment.ExpectedRelativePath;
        Status = assessment.Status.ToString();
        Score = assessment.Score is null
            ? "Not scored — disqualified"
            : assessment.Score.Value.Value.ToString(CultureInfo.InvariantCulture);
        Classification = assessment.Features.Classification.ToString();
        Confidence = assessment.Features.ClassificationConfidence.ToString();
        PageCount = assessment.Features.PageCount?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
        Sampling = assessment.Features.Sampling is null
            ? "Unavailable"
            : assessment.Features.Sampling.InspectedAllPages
                ? $"All {assessment.Features.Sampling.TotalPageCount} pages"
                : $"Sampled {assessment.Features.Sampling.RequestedPageCount} of {assessment.Features.Sampling.TotalPageCount} pages";
        Encryption = assessment.Features.EncryptionStatus.ToString();
        AnalyzerVersion = assessment.AnalyzerVersion.Value;
        ScoringModelVersion = assessment.ScoringModelVersion.Value;
        ClassificationVersion = assessment.Features.ClassificationPolicyVersion.Value;
        ResourceProfileVersion = assessment.Features.ResourceProfileVersion.Value;
        featureSummary = new(() => BuildFeatureSummary(assessment));
        findings = new(() => new ReadOnlyCollection<PdfAssessmentFindingRowViewModel>(
            assessment.Findings.Select(finding => new PdfAssessmentFindingRowViewModel(finding)).ToArray()));
    }

    public long BookId { get; }
    public string BookTitle { get; }
    public string ExpectedRelativePath { get; }
    public string Status { get; }
    public string Score { get; }
    public string Classification { get; }
    public string Confidence { get; }
    public string PageCount { get; }
    public string Sampling { get; }
    public string Encryption { get; }
    public string AnalyzerVersion { get; }
    public string ScoringModelVersion { get; }
    public string ClassificationVersion { get; }
    public string ResourceProfileVersion { get; }
    public string FeatureSummary => featureSummary.Value;
    public IReadOnlyList<PdfAssessmentFindingRowViewModel> Findings => findings.Value;

    private static string BuildFeatureSummary(PdfAssessment assessment)
    {
        PdfFeatureSummary value = assessment.Features;
        string identifiers = value.Identifiers.Count == 0
            ? "none"
            : string.Join(", ", value.Identifiers.Select(identifier => $"{identifier.NormalizedIsbn} ({identifier.Source})"));
        AssessmentFinding? classification = assessment.Findings.SingleOrDefault(finding =>
            finding.RuleId.StartsWith("PDF.CLASSIFICATION.", StringComparison.Ordinal));
        string classificationEvidence = classification is null || classification.Evidence.Count == 0
            ? "none"
            : string.Join(", ", classification.Evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        string selectedPages = value.Sampling is null ? "none" : string.Join(",", value.Sampling.SelectedPages);
        string scoreSummary = assessment.Score is null
            ? "Technical score: not scored; metadata score: not scored"
            : $"Technical score: {assessment.ScoreBreakdown.TechnicalScore}/85; metadata score: {assessment.ScoreBreakdown.EmbeddedMetadataScore}/15";
        string ocrStatement = value.Classification switch
        {
            PdfDocumentClassification.ScannedWithOcr => "A text layer was observed on scan-like sampled pages; OCR provenance is not proven and no OCR was run.",
            PdfDocumentClassification.ScannedWithoutOcr => "Little useful text was observed on scan-like sampled pages; no OCR was run.",
            _ => "No OCR was run and no OCR provenance is claimed.",
        };
        return $"PDF: {value.PdfVersion ?? "unknown"}; Open: {value.OpenStatus}; Text: {value.Text.ExtractionStatus}, {value.Text.UsefulTextPageCount}/{value.Text.AnalyzablePageCount} useful pages, {value.Text.UsefulCharacterCount} useful characters, density {value.Text.DensityBand}; Images: {value.Images.ImageCount} on {value.Images.ImageBearingPageCount}/{value.Images.AnalyzablePageCount} pages, {value.Images.ImageDominantPageCount} image-dominant, balance {value.Images.ContentBalance}; Outline: {(value.OutlinePresent ? $"present ({value.OutlineEntryCount})" : "absent")}; Metadata title: {Describe(value.Metadata.Title)}; author: {Describe(value.Metadata.Author)}; subject: {Describe(value.Metadata.Subject)}; keywords: {Describe(value.Metadata.Keywords)}; Identifiers: {identifiers}; Active/action markers: {value.ActiveContent.JavaScriptOrActionMarkers}; external-reference markers: {value.ActiveContent.ExternalReferenceMarkers}; embedded-file markers: {value.ActiveContent.EmbeddedFileMarkers}; Requested pages: [{selectedPages}]; analyzed: {value.PageAnalysis.AnalyzedPages}; failed: {value.PageAnalysis.FailedPages}; Classification: {value.Classification} ({value.ClassificationConfidence}) — {classification?.Explanation ?? "No classification explanation available."}; evidence: {classificationEvidence}; Extracted glyphs may be hidden, scrambled, or semantically incorrect; {ocrStatement}; Versions: {assessment.AnalyzerVersion}, {assessment.ScoringModelVersion}, {value.ClassificationPolicyVersion}, {value.ResourceProfileVersion}; {scoreSummary}; facts incomplete: {(value.FactsIncomplete ? "yes" : "no")}. Classification is independent of score and sampled evidence does not establish whole-document certainty.";
    }

    private static string Describe(PdfMetadataValue value) => value.Status switch
    {
        PdfMetadataValueStatus.Present => value.Value ?? "present",
        PdfMetadataValueStatus.Truncated => $"{value.Value} (truncated)",
        _ => value.Status.ToString(),
    };
}
