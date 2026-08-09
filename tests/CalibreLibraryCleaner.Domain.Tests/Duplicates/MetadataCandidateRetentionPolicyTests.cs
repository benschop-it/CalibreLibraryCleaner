using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Duplicates;

public sealed class MetadataCandidateRetentionPolicyTests
{
    [Fact]
    public void ExactCleanupEnrichedProjectedRecordBecomesFallbackKeeper()
    {
        CalibreBook lowerId = Book(1, [Present("EPUB", 'a')]);
        CalibreBook enriched = Book(2,
        [
            Present("EPUB", 'b'),
            Projected("PDF", 'c'),
        ]);

        MetadataCandidateRetentionDecision decision = MetadataCandidateRetentionPolicy.Select([
            lowerId,
            enriched,
        ]);

        decision.KeeperBookId.Should().Be(enriched.Id);
        decision.Candidates.Should().ContainSingle(value =>
            value.BookId == enriched.Id && value.AvailableFormatCount == 2);
    }

    [Fact]
    public void LowestRecordIdBreaksCompleteTie()
    {
        CalibreBook first = Book(1, [Present("EPUB", 'a')]);
        CalibreBook second = Book(2, [Present("EPUB", 'b')]);

        MetadataCandidateRetentionPolicy.Select([second, first]).KeeperBookId.Should().Be(first.Id);
    }

    private static CalibreBook Book(long id, BookFormat[] formats) => new(
        new(id),
        "Shared Book",
        "Author",
        [new(new(id), "Author", "Author")],
        [],
        formats,
        $"Author/Book{id}",
        new(languages: ["eng"]));

    private static BookFormat Present(string format, char digest) => new(
        format,
        "book",
        $"Author/book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        new(10, new(new string(digest, 64))),
        new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

    private static BookFormat Projected(string format, char digest) => new(
        format,
        "book",
        string.Empty,
        FormatFileStatus.ProjectedPresent,
        new(10, new(new string(digest, 64))));
}
