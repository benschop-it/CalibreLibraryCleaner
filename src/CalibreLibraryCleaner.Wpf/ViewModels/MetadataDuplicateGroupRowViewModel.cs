using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class MetadataDuplicateGroupRowViewModel : ObservableObject
{
    private bool _isDeferred;

    public MetadataDuplicateGroupRowViewModel(
        ExactMetadataDuplicateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(books);
        GroupId = group.Id;
        NormalizedTitle = group.Identity.Title.Value;
        NormalizedAuthors = group.Identity.Authors.ToString();
        RecordCount = group.Members.Count;
        Category = group.MatchReason.Category;
        Reason = group.MatchReason.Description;
        Members = new ReadOnlyCollection<MetadataDuplicateMemberRowViewModel>(group.Members
            .Select(member =>
            {
                CalibreBook book = books[member];
                return new MetadataDuplicateMemberRowViewModel(
                    book.Id.Value,
                    book.Title,
                    string.Join(" & ", book.Authors.Select(author => author.Name)),
                    book.AuthorSort,
                    string.Join(", ", book.Formats.Select(format => format.Format)),
                    string.Join(", ", book.Identifiers.Select(identifier => $"{identifier.Type}:{identifier.Value}")));
            })
            .ToArray());
        SearchText = string.Join(
            '\n',
            Members.SelectMany(member => new[]
            {
                member.BookId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                member.Title,
                member.Authors,
            }).Prepend(NormalizedAuthors).Prepend(NormalizedTitle));
    }

    public ExactMetadataDuplicateGroupId GroupId { get; }

    public string NormalizedTitle { get; }

    public string NormalizedAuthors { get; }

    public int RecordCount { get; }

    public string Category { get; }

    public string Reason { get; }

    public IReadOnlyList<MetadataDuplicateMemberRowViewModel> Members { get; }

    public bool IsDeferred
    {
        get => _isDeferred;
        private set
        {
            if (SetProperty(ref _isDeferred, value))
            {
                OnPropertyChanged(nameof(ReviewStatus));
            }
        }
    }

    public string ReviewStatus => IsDeferred ? "Deferred" : "Active";

    internal string SearchText { get; }

    internal void SetDeferred(bool isDeferred) => IsDeferred = isDeferred;
}
