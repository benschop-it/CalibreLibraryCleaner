using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class BookRowViewModel
{
    public BookRowViewModel(CalibreBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        Id = book.Id.Value;
        Title = book.Title;
        Authors = string.Join(" & ", book.Authors.Select(author => author.Name));
        AuthorSort = book.AuthorSort;
        RelativeDirectory = book.RelativeDirectory;
        Formats = new ReadOnlyCollection<FormatRowViewModel>(
            book.Formats.Select(format => new FormatRowViewModel(format)).ToArray());
    }

    public long Id { get; }

    public string Title { get; }

    public string Authors { get; }

    public string AuthorSort { get; }

    public string RelativeDirectory { get; }

    public IReadOnlyList<FormatRowViewModel> Formats { get; }
}
