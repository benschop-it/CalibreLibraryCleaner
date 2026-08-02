using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class LibraryStateDeltaPolicyTests
{
    private static readonly DateTimeOffset ScannedAt = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly FormatFileFingerprint Duplicate = new(10, new(new string('a', 64)));

    [Fact]
    public void RemoveFormatThenEmptyRecordAdvancesRevisionWithoutRescan()
    {
        LibraryState baseline = LibraryState.FromScan(Snapshot(), Generation);

        LibraryState formatRemoved = LibraryStateDeltaPolicy.Apply(baseline, new RemoveFormatLibraryStateDelta(
            Generation, new(0), "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Duplicate));
        LibraryState recordRemoved = LibraryStateDeltaPolicy.Apply(formatRemoved, new RemoveRecordLibraryStateDelta(
            Generation, new(1), "remove-record:2", ScannedAt.AddSeconds(2), new(2)));

        formatRemoved.Revision.Should().Be(new LibraryStateRevision(1));
        formatRemoved.Snapshot.Books.Single(value => value.Id == new CalibreBookId(2)).Formats.Should().BeEmpty();
        formatRemoved.Snapshot.ExactBinaryDuplicateGroups.Should().BeEmpty();
        recordRemoved.Revision.Should().Be(new LibraryStateRevision(2));
        recordRemoved.Snapshot.Books.Select(value => value.Id).Should().Equal(new CalibreBookId(1));
        recordRemoved.Snapshot.ScannedAt.Should().Be(ScannedAt);
        recordRemoved.ProjectedAtUtc.Should().Be(ScannedAt.AddSeconds(2));
    }

    [Fact]
    public void RemoveRecordWithRemainingFormatIsRejected()
    {
        LibraryState baseline = LibraryState.FromScan(Snapshot(), Generation);

        Action act = () => LibraryStateDeltaPolicy.Apply(baseline, new RemoveRecordLibraryStateDelta(
            Generation, new(0), "remove-record:2", ScannedAt.AddSeconds(1), new(2)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*only after all formats are absent*");
    }

    [Fact]
    public void StaleRevisionIsRejected()
    {
        LibraryState baseline = LibraryState.FromScan(Snapshot(), Generation);

        Action act = () => LibraryStateDeltaPolicy.Apply(baseline, new RemoveFormatLibraryStateDelta(
            Generation, new(7), "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Duplicate));

        act.Should().Throw<InvalidOperationException>().WithMessage("*different library-state revision*");
    }

    [Fact]
    public void UncertainStateBlocksFurtherDeltas()
    {
        LibraryState uncertain = LibraryState.FromScan(Snapshot(), Generation).MarkUncertain(new(
            "COMMAND_OUTCOME_AMBIGUOUS", "Calibre returned an ambiguous result.",
            ScannedAt.AddSeconds(1), "remove-format:2:EPUB"));

        Action act = () => LibraryStateDeltaPolicy.Apply(uncertain, new RemoveFormatLibraryStateDelta(
            Generation, new(0), "remove-format:2:EPUB", ScannedAt.AddSeconds(2), new(2), "EPUB", Duplicate));

        act.Should().Throw<InvalidOperationException>().WithMessage("*state is uncertain*");
    }

    [Fact]
    public void FingerprintMismatchIsRejected()
    {
        LibraryState baseline = LibraryState.FromScan(Snapshot(), Generation);
        FormatFileFingerprint unexpected = new(10, new(new string('b', 64)));

        Action act = () => LibraryStateDeltaPolicy.Apply(baseline, new RemoveFormatLibraryStateDelta(
            Generation, new(0), "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", unexpected));

        act.Should().Throw<InvalidOperationException>().WithMessage("*fingerprint does not match*");
    }

    private static LibrarySnapshot Snapshot()
    {
        CalibreBook[] books = [Book(1), Book(2)];
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
            ScannedAt,
            books,
            [],
            ExactBinaryDuplicateDetector.Detect(books));
    }

    private static CalibreBook Book(long id)
    {
        string directory = $"Author/Book ({id})";
        BookFormat format = new("EPUB", "book", $"{directory}/book.epub", FormatFileStatus.Present,
            Duplicate, new(Duplicate.SizeInBytes, ScannedAt, ScannedAt, 0));
        return new(new(id), "Book", "Author", [new(new(id), "Author", "Author")], [], [format], directory);
    }
}
