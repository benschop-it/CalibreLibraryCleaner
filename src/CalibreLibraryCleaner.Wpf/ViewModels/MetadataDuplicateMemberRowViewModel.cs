namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed record MetadataDuplicateMemberRowViewModel(
    long BookId,
    string Title,
    string Authors,
    string AuthorSort,
    string Formats,
    string Identifiers);
