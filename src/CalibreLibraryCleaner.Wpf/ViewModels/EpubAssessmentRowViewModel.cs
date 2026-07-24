using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class EpubAssessmentRowViewModel
{
    private readonly Lazy<IReadOnlyList<EpubAssessmentFindingRowViewModel>> _findings;
    private readonly Lazy<string> _featureSummary;

    public EpubAssessmentRowViewModel(FormatAssessment assessment, CalibreBook? book)
    {
        BookId = assessment.CalibreBookId.Value;
        BookTitle = book?.Title ?? string.Empty;
        ExpectedRelativePath = assessment.ExpectedRelativePath;
        Status = assessment.Status.ToString();
        Score = assessment.Score is null ? "Not scored — disqualified" : assessment.Score.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Opened = assessment.Features.Opened ? "Yes" : "No";
        PackageParsed = assessment.Features.PackageParsed ? "Yes" : "No";
        PackageVersion = assessment.Features.PackageVersion ?? "Unknown";
        AnalyzerVersion = assessment.AnalyzerVersion.Value;
        ScoringModelVersion = assessment.ScoringModelVersion.Value;
        _featureSummary = new(() => $"Embedded title: {assessment.Features.EmbeddedTitle ?? "(missing)"}; Authors: {string.Join(", ", assessment.Features.Authors)}; Languages: {string.Join(", ", assessment.Features.Languages)}; Dates: {string.Join(", ", assessment.Features.Dates)}; Strong identifiers: {string.Join(", ", assessment.Features.StrongIdentifiers)}; Cover: {(assessment.Features.CoverPresent ? "present" : "missing")} {assessment.Features.CoverWidth}×{assessment.Features.CoverHeight}; Navigation: {(assessment.Features.NavigationPresent ? "present" : "missing")}; Manifest: {assessment.Features.ManifestItemCount}; Spine: {assessment.Features.SpineItemCount}; Chapters: {assessment.Features.ChapterCount}; Local resources: {assessment.Features.LocalResourceCount}; Broken references: {assessment.Features.BrokenReferenceCount}; Readable characters: {assessment.Features.ReadableCharacterCount}; Encryption: {assessment.Features.EncryptionState}; Truncated: {(assessment.Features.AnalysisTruncated ? "yes" : "no")}");
        _findings = new(() => new ReadOnlyCollection<EpubAssessmentFindingRowViewModel>(
            assessment.Findings.Select(finding => new EpubAssessmentFindingRowViewModel(finding)).ToArray()));
    }

    public long BookId { get; }
    public string BookTitle { get; }
    public string ExpectedRelativePath { get; }
    public string Status { get; }
    public string Score { get; }
    public string Opened { get; }
    public string PackageParsed { get; }
    public string PackageVersion { get; }
    public string AnalyzerVersion { get; }
    public string ScoringModelVersion { get; }
    public string FeatureSummary => _featureSummary.Value;
    public IReadOnlyList<EpubAssessmentFindingRowViewModel> Findings => _findings.Value;
}
