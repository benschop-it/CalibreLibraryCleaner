using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactDuplicateGroupRowViewModel : ObservableObject
{
    private readonly IReadOnlyList<CalibreBookId> _recordIdsToDelete;

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
        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy.Select([group], books.Values).Single();
        IsCleanupEligible = decision.IsEligible && group.SpansMultipleBookRecords;
        SkipReason = decision.SkipReason;
        RetainedMember = decision.RetainedMember is null
            ? null
            : Members.Single(value => value.Member == decision.RetainedMember);
        foreach (ExactDuplicateMemberRowViewModel member in Members)
        {
            member.IsRetained = member == RetainedMember;
            member.IsSkipped = !decision.IsEligible;
        }
        _recordIdsToDelete = decision.FormatRemovals
            .Where(value => books[value.BookId].Formats.Count == 1)
            .Select(value => value.BookId)
            .Distinct()
            .OrderBy(value => value.Value)
            .ToArray();
    }

    public ExactBinaryDuplicateGroupId GroupId { get; }

    public string Id { get; }

    public string FileSize { get; }

    public string Sha256 { get; }

    public int FileCount { get; }

    public int RecordCount { get; }

    public IReadOnlyList<ExactDuplicateMemberRowViewModel> Members { get; }

    public ExactDuplicateMemberRowViewModel? RetainedMember { get; }

    public bool IsCleanupEligible { get; }

    public string? SkipReason { get; }

    public IReadOnlyList<CalibreBookId> RecordIdsToDelete => _recordIdsToDelete;
}
