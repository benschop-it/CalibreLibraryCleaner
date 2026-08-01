using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactDuplicateGroupRowViewModel : ObservableObject
{
    private ExactDuplicateMemberRowViewModel? _retainedMember;

    public ExactDuplicateGroupRowViewModel(
        ExactBinaryDuplicateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books)
    {
        GroupId = group.Id;
        Id = group.Id.Value;
        FileSize = $"{group.Fingerprint.SizeInBytes:N0} bytes";
        Sha256 = group.Fingerprint.Sha256.Value;
        FileCount = group.Members.Count;
        RecordCount = group.DistinctBookCount;
        Members = new ReadOnlyCollection<ExactDuplicateMemberRowViewModel>(group.Members
            .Select(member =>
            {
                CalibreBook book = books[member.BookId];
                return new ExactDuplicateMemberRowViewModel(
                    member,
                    book.Title,
                    string.Join(" & ", book.Authors.Select(author => author.Name)),
                    member.Format);
            })
            .ToArray());
        RetainedMember = Members[0];
    }

    public ExactBinaryDuplicateGroupId GroupId { get; }

    public string Id { get; }

    public string FileSize { get; }

    public string Sha256 { get; }

    public int FileCount { get; }

    public int RecordCount { get; }

    public IReadOnlyList<ExactDuplicateMemberRowViewModel> Members { get; }

    public ExactDuplicateMemberRowViewModel RetainedMember
    {
        get => _retainedMember!;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Members.Contains(value))
            {
                throw new ArgumentException("The retained file must belong to this exact duplicate group.", nameof(value));
            }

            if (!SetProperty(ref _retainedMember, value)) return;
            foreach (ExactDuplicateMemberRowViewModel member in Members)
            {
                member.IsRetained = member.BookId == value.BookId;
            }
            OnPropertyChanged(nameof(RecordIdsToDelete));
        }
    }

    public IReadOnlyList<CalibreBookId> RecordIdsToDelete => Members
        .Where(value => value.BookId != RetainedMember.BookId)
        .Select(value => value.Member.BookId)
        .Distinct()
        .OrderBy(value => value.Value)
        .ToArray();
}
