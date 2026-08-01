using CalibreLibraryCleaner.Domain.Duplicates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactDuplicateMemberRowViewModel(
    ExactBinaryDuplicateMember member,
    string title,
    string authors,
    string format) : ObservableObject
{
    private bool _isRetained;
    private bool _isMarkedForDeletion;

    public ExactBinaryDuplicateMember Member { get; } = member;
    public long BookId { get; } = member.BookId.Value;
    public string Title { get; } = title;
    public string Authors { get; } = authors;
    public string Format { get; } = format;
    public string ExpectedRelativePath { get; } = member.ExpectedRelativePath;

    public bool IsRetained
    {
        get => _isRetained;
        internal set => SetProperty(ref _isRetained, value);
    }

    public bool IsMarkedForDeletion
    {
        get => _isMarkedForDeletion;
        set => SetProperty(ref _isMarkedForDeletion, value);
    }
}
