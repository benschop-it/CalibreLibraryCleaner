using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class EpubAssessmentRowViewModel
{
    private readonly Lazy<IReadOnlyList<EpubAssessmentFindingRowViewModel>> _findings;
    private readonly Lazy<string> _featureSummary;

    public EpubAssessmentRowViewModel(EpubAssessment assessment, CalibreBook? book)
    {
        BookId = assessment.CalibreBookId.Value;
        BookTitle = book?.Title ?? string.Empty;
        ExpectedRelativePath = assessment.ExpectedRelativePath;
        Status = assessment.Status.ToString();
        ScoreSortValue = assessment.Score?.Value;
        Score = assessment.Status switch
        {
            AssessmentStatus.Unassessed => "Not scored — unassessed",
            AssessmentStatus.Disqualified => "Not scored — disqualified",
            _ when assessment.ScoreCap is not null => $"{assessment.Score!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} (fallback; max {assessment.ScoreCap.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})",
            _ => assessment.Score!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        Coverage = assessment.Features.Coverage.ToString();
        Opened = assessment.Features.Opened ? "Yes" : "No";
        PackageParsed = assessment.Features.PackageParsed ? "Yes" : "No";
        PackageVersion = assessment.Features.PackageVersion ?? "Unknown";
        AnalyzerVersion = assessment.AnalyzerVersion.Value;
        ScoringModelVersion = assessment.ScoringModelVersion.Value;
        _featureSummary = new(() => $"Coverage: {assessment.Features.Coverage}; Available facets: {assessment.Features.AvailableFacets}; Uncapped score: {assessment.UncappedScore?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not scored"}; Score cap: {assessment.ScoreCap?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}; Fallback candidates: {assessment.Features.FallbackCandidateCount}; Renderable candidates: {assessment.Features.FallbackRenderableCount}; Renderable evidence: {assessment.Features.RenderableEvidence}; Embedded title: {assessment.Features.EmbeddedTitle ?? "(missing or unassessed)"}; Authors: {string.Join(", ", assessment.Features.Authors)}; Languages: {string.Join(", ", assessment.Features.Languages)}; Dates: {string.Join(", ", assessment.Features.Dates)}; Strong identifiers: {string.Join(", ", assessment.Features.StrongIdentifiers)}; Cover: {(assessment.Features.CoverPresent ? "present" : "missing or unassessed")} {assessment.Features.CoverWidth}×{assessment.Features.CoverHeight}; Navigation: {(assessment.Features.NavigationPresent ? "present" : "missing or unassessed")}; Manifest: {assessment.Features.ManifestItemCount}; Spine: {assessment.Features.SpineItemCount}; Chapters: {assessment.Features.ChapterCount}; Local resources: {assessment.Features.LocalResourceCount}; Broken references: {assessment.Features.BrokenReferenceCount}; Readable characters: {assessment.Features.ReadableCharacterCount}; Encryption: {assessment.Features.EncryptionState}; Truncated: {(assessment.Features.AnalysisTruncated ? "yes" : "no")}");
        _findings = new(() => new ReadOnlyCollection<EpubAssessmentFindingRowViewModel>(
            assessment.Findings.Select(finding => new EpubAssessmentFindingRowViewModel(finding)).ToArray()));
    }

    public long BookId { get; }
    public string BookTitle { get; }
    public string ExpectedRelativePath { get; }
    public string Status { get; }
    public string Score { get; }
    public int? ScoreSortValue { get; }
    public string Coverage { get; }
    public string Opened { get; }
    public string PackageParsed { get; }
    public string PackageVersion { get; }
    public string AnalyzerVersion { get; }
    public string ScoringModelVersion { get; }
    public string FeatureSummary => _featureSummary.Value;
    public IReadOnlyList<EpubAssessmentFindingRowViewModel> Findings => _findings.Value;
}
