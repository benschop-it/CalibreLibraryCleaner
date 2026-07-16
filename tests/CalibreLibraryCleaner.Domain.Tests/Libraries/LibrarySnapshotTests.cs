using CalibreLibraryCleaner.Domain.Duplicates;
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
            new LibraryIdentity(Guid.NewGuid().ToString(), 27, "library"),
            DateTimeOffset.UnixEpoch,
            books,
            findings);

        books.Clear();
        findings.Add(new("TEST", FindingSeverity.Warning, "Message", "Action"));

        snapshot.Books.Should().ContainSingle();
        snapshot.Findings.Should().BeEmpty();
    }

    [Fact]
    public void SnapshotDefensivelyCopiesMetadataDuplicateGroups()
    {
        CalibreBook first = CreateBook();
        CalibreBook second = new(
            new CalibreBookId(2),
            first.Title,
            first.AuthorSort,
            first.Authors,
            first.Identifiers,
            first.Formats,
            "Author/Book 2");
        List<ExactMetadataDuplicateGroup> groups = ExactMetadataDuplicateDetector.Detect([first, second]).ToList();
        LibrarySnapshot snapshot = new(
            new LibraryIdentity(Guid.NewGuid().ToString(), 27, "library"),
            DateTimeOffset.UnixEpoch,
            [first, second],
            [],
            [],
            groups);

        groups.Clear();

        snapshot.ExactMetadataDuplicateGroups.Should().ContainSingle();
    }

    private static CalibreBook CreateBook() => new(
        new CalibreBookId(1),
        "Book",
        "Author",
        [new BookAuthor(new CalibreAuthorId(1), "Author", "Author")],
        [new BookIdentifier("isbn", "123")],
        [new BookFormat(
            "EPUB",
            "Book",
            "Author/Book/Book.epub",
            FormatFileStatus.Present,
            new FormatFileFingerprint(4, new Sha256Digest(new string('a', 64))))],
        "Author/Book");
}
