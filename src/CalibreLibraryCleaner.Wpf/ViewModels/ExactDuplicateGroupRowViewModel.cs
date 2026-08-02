using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactDuplicateGroupRowViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<CalibreBookId, CalibreBook> _books;
    private ExactDuplicateMemberRowViewModel? _retainedMember;

    public ExactDuplicateGroupRowViewModel(
        ExactBinaryDuplicateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books)
    {
        _books = books;
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
        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy.Select([group], books.Values).Single();
        IsCleanupEligible = decision.IsEligible && group.SpansMultipleBookRecords;
        SkipReason = decision.SkipReason;
        _retainedMember = decision.RetainedMember is null
            ? null
            : Members.Single(value => value.Member == decision.RetainedMember);
        foreach (ExactDuplicateMemberRowViewModel member in Members)
        {
            member.IsRetained = member == _retainedMember;
            member.IsSkipped = !decision.IsEligible;
        }
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
            if (value is null || !Members.Contains(value) || !IsCleanupEligible) return;
            if (!SetProperty(ref _retainedMember, value)) return;
            foreach (ExactDuplicateMemberRowViewModel member in Members)
                member.IsRetained = member == value;
            OnPropertyChanged(nameof(RecordIdsToDelete));
        }
    }

    public bool IsCleanupEligible { get; }

    public string? SkipReason { get; }

    public IReadOnlyList<CalibreBookId> RecordIdsToDelete => RetainedMember is null
        ? []
        : Members.Where(value => value != RetainedMember && _books[value.Member.BookId].Formats.Count == 1)
            .Select(value => value.Member.BookId)
            .Distinct()
            .OrderBy(value => value.Value)
            .ToArray();
}
