using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Duplicates;

public sealed class ExactBinaryDuplicateDetectorTests
{
    [Fact]
    public void DetectGroupsMatchingSizeAndDigestInDeterministicOrder()
    {
        FormatFileFingerprint duplicate = new(10, new Sha256Digest(new string('b', 64)));
        FormatFileFingerprint sameSizeDifferentHash = new(10, new Sha256Digest(new string('a', 64)));
        CalibreBook[] books =
        [
            CreateBook(2, "PDF", "b.pdf", duplicate),
            CreateBook(3, "EPUB", "c.epub", sameSizeDifferentHash),
            CreateBook(1, "EPUB", "a.epub", duplicate),
        ];

        IReadOnlyList<ExactBinaryDuplicateGroup> groups = ExactBinaryDuplicateDetector.Detect(books);

        groups.Should().ContainSingle();
        groups[0].Members.Select(member => member.BookId.Value).Should().Equal(1, 2);
        groups[0].DistinctBookCount.Should().Be(2);
        groups[0].Id.Value.Should().Be($"sha256:{new string('b', 64)}:10");
    }

    [Fact]
    public void DetectAllowsDistinctFilesOnSameRecordWithoutCallingRecordDuplicate()
    {
        FormatFileFingerprint fingerprint = new(3, new Sha256Digest(new string('c', 64)));
        CalibreBook book = CreateBook(
            1,
            [
                Present("EPUB", "Book.epub", fingerprint),
                Present("PDF", "Book.pdf", fingerprint),
            ]);

        ExactBinaryDuplicateGroup group = ExactBinaryDuplicateDetector.Detect([book]).Single();

        group.DistinctBookCount.Should().Be(1);
        group.SpansMultipleBookRecords.Should().BeFalse();
    }

    [Fact]
    public void DetectObservesCancellationWhileEnumeratingBooks()
    {
        using CancellationTokenSource cancellation = new();
        FormatFileFingerprint fingerprint = new(3, new Sha256Digest(new string('c', 64)));

        Action act = () => ExactBinaryDuplicateDetector.Detect(Books(), cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        return;

        IEnumerable<CalibreBook> Books()
        {
            yield return CreateBook(1, "EPUB", "a.epub", fingerprint);
            cancellation.Cancel();
            yield return CreateBook(2, "EPUB", "b.epub", fingerprint);
        }
    }

    [Fact]
    public void DetectProducesSameGroupsForShuffledInput()
    {
        FormatFileFingerprint larger = new(20, new Sha256Digest(new string('b', 64)));
        FormatFileFingerprint smaller = new(10, new Sha256Digest(new string('a', 64)));
        CalibreBook[] books =
        [
            CreateBook(4, "PDF", "d.pdf", smaller),
            CreateBook(2, "EPUB", "b.epub", larger),
            CreateBook(3, "PDF", "c.pdf", smaller),
            CreateBook(1, "EPUB", "a.epub", larger),
        ];

        IReadOnlyList<ExactBinaryDuplicateGroup> forward = ExactBinaryDuplicateDetector.Detect(books);
        IReadOnlyList<ExactBinaryDuplicateGroup> reverse = ExactBinaryDuplicateDetector.Detect(books.Reverse());

        reverse.Select(group => group.Id).Should().Equal(forward.Select(group => group.Id));
        reverse.SelectMany(group => group.Members.Select(member => member.BookId))
            .Should().Equal(forward.SelectMany(group => group.Members.Select(member => member.BookId)));
    }

    private static CalibreBook CreateBook(
        long id,
        string format,
        string path,
        FormatFileFingerprint fingerprint) => CreateBook(
        id,
        [Present(format, path, fingerprint)]);

    private static BookFormat Present(string format, string path, FormatFileFingerprint fingerprint) => new(
        format,
        "Book",
        path,
        FormatFileStatus.Present,
        fingerprint,
        new FormatFileObservation(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

    private static CalibreBook CreateBook(long id, IEnumerable<BookFormat> formats) => new(
        new CalibreBookId(id),
        $"Book {id}",
        "Author",
        [new BookAuthor(new CalibreAuthorId(id), "Author", "Author")],
        [],
        formats,
        $"Book {id}");
}
