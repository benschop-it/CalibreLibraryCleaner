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
    private bool _isSkipped;

    public ExactBinaryDuplicateMember Member { get; } = member;
    public long BookId { get; } = member.BookId.Value;
    public string Title { get; } = title;
    public string Authors { get; } = authors;
    public string Format { get; } = format;
    public string ExpectedRelativePath { get; } = member.ExpectedRelativePath;

    public bool IsRetained
    {
        get => _isRetained;
        internal set
        {
            if (SetProperty(ref _isRetained, value)) OnPropertyChanged(nameof(CleanupAction));
        }
    }

    public bool IsSkipped
    {
        get => _isSkipped;
        internal set
        {
            if (SetProperty(ref _isSkipped, value)) OnPropertyChanged(nameof(CleanupAction));
        }
    }

    public string CleanupAction => IsSkipped ? "Skip anomaly" : IsRetained ? "Keep" : "Remove format";
}
