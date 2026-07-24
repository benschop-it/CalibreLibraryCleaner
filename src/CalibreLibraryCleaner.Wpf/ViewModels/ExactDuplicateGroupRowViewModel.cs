using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactDuplicateGroupRowViewModel
{
    public ExactDuplicateGroupRowViewModel(
        ExactBinaryDuplicateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books)
    {
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
                    member.BookId.Value,
                    book.Title,
                    string.Join(" & ", book.Authors.Select(author => author.Name)),
                    member.Format,
                    member.ExpectedRelativePath);
            })
            .ToArray());
    }

    public string Id { get; }

    public string FileSize { get; }

    public string Sha256 { get; }

    public int FileCount { get; }

    public int RecordCount { get; }

    public IReadOnlyList<ExactDuplicateMemberRowViewModel> Members { get; }
}
