using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class LibrarySnapshotTests
{
    [Fact]
    public void SnapshotDefensivelyCopiesCollections()
    {
        List<CalibreBook> books = [CreateBook()];
        List<LibraryFinding> findings = [];
        LibrarySnapshot snapshot = new(
            new LibraryIdentity(Guid.NewGuid().ToString(), 26, "library"),
            DateTimeOffset.UnixEpoch,
            books,
            findings);

        books.Clear();
        findings.Add(new("TEST", FindingSeverity.Warning, "Message", "Action"));

        snapshot.Books.Should().ContainSingle();
        snapshot.Findings.Should().BeEmpty();
    }

    private static CalibreBook CreateBook() => new(
        new CalibreBookId(1),
        "Book",
        "Author",
        [new BookAuthor(new CalibreAuthorId(1), "Author", "Author")],
        [new BookIdentifier("isbn", "123")],
        [new BookFormat("EPUB", "Book", "Author/Book/Book.epub", FormatFileStatus.Present)],
        "Author/Book");
}
