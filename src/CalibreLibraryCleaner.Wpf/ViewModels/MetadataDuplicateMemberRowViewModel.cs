using CalibreLibraryCleaner.Domain.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class MetadataDuplicateMemberRowViewModel : ObservableObject
{
    private bool _isRetainedSeparate;
    private bool _isKeeper;

    public MetadataDuplicateMemberRowViewModel(
        long bookId,
        string title,
        string authors,
        string authorSort,
        string formats,
        string identifiers,
        string publisher = "",
        string publicationDate = "",
        string languages = "",
        string series = "",
        string hasCover = "No",
        string metadataQualityFacts = "Not ranked",
        IReadOnlyList<BookFormat>? bookFormats = null)
    {
        BookId = bookId;
        Title = title;
        Authors = authors;
        AuthorSort = authorSort;
        Formats = formats;
        Identifiers = identifiers;
        Publisher = publisher;
        PublicationDate = publicationDate;
        Languages = languages;
        Series = series;
        HasCover = hasCover;
        MetadataQualityFacts = metadataQualityFacts;
        BookFormat? launchFormat = (bookFormats ?? [])
            .Where(value => value.FileStatus == FormatFileStatus.Present)
            .OrderBy(value => FormatPreference(value.Format))
            .ThenBy(value => value.Format, StringComparer.Ordinal)
            .FirstOrDefault();
        LaunchFormat = launchFormat?.Format;
        LaunchRelativePath = launchFormat?.ExpectedRelativePath;
    }

    public long BookId { get; }
    public string Title { get; }
    public string Authors { get; }
    public string AuthorSort { get; }
    public string Formats { get; }
    public string Identifiers { get; }
    public string Publisher { get; }
    public string PublicationDate { get; }
    public string Languages { get; }
    public string Series { get; }
    public string HasCover { get; }
    public string MetadataQualityFacts { get; }
    public string? LaunchFormat { get; }
    public string? LaunchRelativePath { get; }
    public string Action => IsKeeper ? "Keep" : "Remove";

    public bool IsKeeper
    {
        get => _isKeeper;
        internal set
        {
            if (SetProperty(ref _isKeeper, value)) OnPropertyChanged(nameof(Action));
        }
    }

    public bool IsRetainedSeparate
    {
        get => _isRetainedSeparate;
        set => SetProperty(ref _isRetainedSeparate, value);
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
