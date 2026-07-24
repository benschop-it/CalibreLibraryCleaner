namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed record ExactDuplicateMemberRowViewModel(
    long BookId,
    string Title,
    string Authors,
    string Format,
    string ExpectedRelativePath);
