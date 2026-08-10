using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class UnifiedCandidateMemberRowViewModel : ObservableObject
{
    private bool _isKeeper;

    public UnifiedCandidateMemberRowViewModel(
        CalibreBook book,
        ExpandedCandidateRetentionCandidate retention)
    {
        ArgumentNullException.ThrowIfNull(book);
        BookId = book.Id.Value;
        Title = book.Title;
        Authors = string.Join(", ", book.Authors.Select(value => value.Name));
        Languages = book.PublicationMetadata.Languages.Count == 0
            ? "Unknown"
            : string.Join(", ", book.PublicationMetadata.Languages);
        Series = string.IsNullOrWhiteSpace(book.PublicationMetadata.Series)
            ? string.Empty
            : book.PublicationMetadata.SeriesIndex is null
                ? book.PublicationMetadata.Series
                : $"{book.PublicationMetadata.Series} #{book.PublicationMetadata.SeriesIndex}";
        Formats = string.Join(", ", book.Formats.OrderBy(value => value.Format, StringComparer.Ordinal)
            .Select(value => $"{value.Format} ({value.FileStatus})"));
        BookFormat? launchFormat = book.Formats
            .Where(value => value.FileStatus == FormatFileStatus.Present)
            .OrderBy(value => FormatPreference(value.Format))
            .ThenBy(value => value.Format, StringComparer.Ordinal)
            .FirstOrDefault();
        LaunchFormat = launchFormat?.Format;
        LaunchRelativePath = launchFormat?.ExpectedRelativePath;
        RetentionFacts = $"Assessed formats: {retention.CompletedAssessmentCount}; score total: {retention.AssessmentScoreTotal}; present formats: {retention.PresentFormatCount}; metadata completeness: {retention.MetadataCompletenessCount}; valid identifiers: {retention.ValidStrongIdentifierCount}; cover: {(retention.HasCover ? "Yes" : "No")}";
    }

    public long BookId { get; }
    public string Action => IsKeeper ? "Keep" : "Remove";
    public string Title { get; }
    public string Authors { get; }
    public string Languages { get; }
    public string Series { get; }
    public string Formats { get; }
    public string? LaunchFormat { get; }
    public string? LaunchRelativePath { get; }
    public string RetentionFacts { get; }

    public bool IsKeeper
    {
        get => _isKeeper;
        internal set
        {
            if (SetProperty(ref _isKeeper, value)) OnPropertyChanged(nameof(Action));
        }
    }

    private static int FormatPreference(string format) => format switch
    {
        "EPUB" => 0,
        "AZW3" => 1,
        "MOBI" => 2,
        "PDF" => 3,
        _ => 4,
    };
}
