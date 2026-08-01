using System.Collections.ObjectModel;
using System.ComponentModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactDuplicateGroupRowViewModel : ObservableObject
{
    private ExactDuplicateMemberRowViewModel? _retainedMember;
    private bool _updatingDeletionMarks;

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
        foreach (ExactDuplicateMemberRowViewModel member in Members)
            member.PropertyChanged += OnMemberPropertyChanged;
    }

    public ExactBinaryDuplicateGroupId GroupId { get; }

    public string Id { get; }

    public string FileSize { get; }

    public string Sha256 { get; }

    public int FileCount { get; }

    public int RecordCount { get; }

    public IReadOnlyList<ExactDuplicateMemberRowViewModel> Members { get; }

    public ExactDuplicateMemberRowViewModel? RetainedMember
    {
        get => _retainedMember;
        set
        {
            if (value is not null && !Members.Contains(value))
            {
                throw new ArgumentException("The retained file must belong to this exact duplicate group.", nameof(value));
            }

            if (!SetProperty(ref _retainedMember, value)) return;
            foreach (ExactDuplicateMemberRowViewModel member in Members)
            {
                member.IsRetained = ReferenceEquals(member, value);
            }
            if (value is not null) SetRecordDeletionMark(value.BookId, false);
            OnPropertyChanged(nameof(MarkedRecordIds));
        }
    }

    public IReadOnlyList<CalibreBookId> MarkedRecordIds => Members
        .Where(value => value.IsMarkedForDeletion)
        .Select(value => value.Member.BookId)
        .Distinct()
        .OrderBy(value => value.Value)
        .ToArray();

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_updatingDeletionMarks || eventArgs.PropertyName != nameof(ExactDuplicateMemberRowViewModel.IsMarkedForDeletion)
            || sender is not ExactDuplicateMemberRowViewModel member) return;
        bool marked = member.IsMarkedForDeletion && member.BookId != RetainedMember?.BookId;
        SetRecordDeletionMark(member.BookId, marked);
        OnPropertyChanged(nameof(MarkedRecordIds));
    }

    private void SetRecordDeletionMark(long bookId, bool marked)
    {
        _updatingDeletionMarks = true;
        try
        {
            foreach (ExactDuplicateMemberRowViewModel member in Members.Where(value => value.BookId == bookId))
                member.IsMarkedForDeletion = marked;
        }
        finally
        {
            _updatingDeletionMarks = false;
        }
    }
}
