using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExpandedCandidateMemberRowViewModel
{
    public ExpandedCandidateMemberRowViewModel(CalibreBook book, bool isAnchor)
    {
        ArgumentNullException.ThrowIfNull(book);
        BookId = book.Id.Value;
        Role = isAnchor ? "Anchor" : "Related";
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
    }

    public long BookId { get; }
    public string Role { get; }
    public string Title { get; }
    public string Authors { get; }
    public string Languages { get; }
    public string Series { get; }
    public string Formats { get; }
    public string? LaunchFormat { get; }
    public string? LaunchRelativePath { get; }

    private static int FormatPreference(string format) => format switch
    {
        "EPUB" => 0,
        "AZW3" => 1,
        "MOBI" => 2,
        "PDF" => 3,
        _ => 4,
    };
}
