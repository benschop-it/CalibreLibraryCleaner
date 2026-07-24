using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class MetadataDuplicateMemberRowViewModel : ObservableObject
{
    private bool _isRetainedSeparate;

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
        string metadataQualityFacts = "Not ranked")
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

    public bool IsRetainedSeparate
    {
        get => _isRetainedSeparate;
        set => SetProperty(ref _isRetainedSeparate, value);
    }
}
