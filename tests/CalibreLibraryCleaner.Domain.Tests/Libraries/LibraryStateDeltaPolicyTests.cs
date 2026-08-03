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

    [Fact]
    public void AddFormatCreatesProjectedFactWithoutInventingManagedPath()
    {
        LibraryState baseline = LibraryState.FromScan(Snapshot(), Generation);
        FormatFileFingerprint pdf = new(20, new(new string('b', 64)));

        LibraryState projected = LibraryStateDeltaPolicy.Apply(baseline,
            new AddOrReplaceFormatLibraryStateDelta(Generation, new(0), "add-format:1:PDF",
                ScannedAt.AddSeconds(1), new(1), "PDF", pdf, null));

        BookFormat format = projected.Snapshot.Books.Single(value => value.Id == new CalibreBookId(1))
            .Formats.Single(value => value.Format == "PDF");
        format.FileStatus.Should().Be(FormatFileStatus.ProjectedPresent);
        format.Fingerprint.Should().Be(pdf);
        format.Observation.Should().BeNull();
        format.ExpectedRelativePath.Should().BeEmpty();
    }

    [Fact]
    public void RecordWithContentRequiresExplicitConsolidationDelta()
    {
        LibraryState baseline = LibraryState.FromScan(Snapshot(), Generation);

        LibraryState projected = LibraryStateDeltaPolicy.Apply(baseline,
            new RemoveRecordWithContentLibraryStateDelta(Generation, new(0), "remove-source:2",
                ScannedAt.AddSeconds(1), new(2)));

        projected.Snapshot.Books.Select(value => value.Id).Should().Equal(new CalibreBookId(1));
        projected.Snapshot.ExactBinaryDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public void ExactCleanupBatchIsEquivalentToSequentialProjection()
    {
        FormatFileFingerprint pdf = new(20, new(new string('b', 64)));
        CalibreBook source = new(new(2), "Book", "Author", [new(new(2), "Author", "Author")], [],
        [
            Book(2).Formats.Single(),
            new("PDF", "book", "Author/Book (2)/book.pdf", FormatFileStatus.Present,
                pdf, new(pdf.SizeInBytes, ScannedAt, ScannedAt, 0)),
        ], "Author/Book (2)");
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
            ScannedAt, [Book(1), source], [], ExactBinaryDuplicateDetector.Detect([Book(1), source]));
        LibraryState baseline = LibraryState.FromScan(snapshot, Generation);
        LibraryStateDelta[] deltas =
        [
            new AddOrReplaceFormatLibraryStateDelta(Generation, new(0), "add:1:PDF",
                ScannedAt.AddSeconds(1), new(1), "PDF", pdf, null),
            new RemoveFormatLibraryStateDelta(Generation, new(1), "remove:2:EPUB",
                ScannedAt.AddSeconds(1), new(2), "EPUB", Duplicate),
            new RemoveFormatLibraryStateDelta(Generation, new(2), "remove:2:PDF",
                ScannedAt.AddSeconds(1), new(2), "PDF", pdf),
            new RemoveRecordLibraryStateDelta(Generation, new(3), "remove-record:2",
                ScannedAt.AddSeconds(1), new(2)),
        ];
        LibraryState sequential = baseline;
        foreach (LibraryStateDelta delta in deltas) sequential = LibraryStateDeltaPolicy.Apply(sequential, delta);

        LibraryState batched = LibraryStateDeltaPolicy.ApplyBatch(baseline, deltas);

        batched.Should().BeEquivalentTo(sequential);
    }

    [Fact]
    public void SixThousandDeltaBatchProjectsTenThousandRecordLibraryWithoutRescan()
    {
        const int recordCount = 10_000;
        const int removalCount = 6_000;
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\large-library"),
            ScannedAt,
            Enumerable.Range(1, recordCount).Select(id => new CalibreBook(
                new(id), $"Book {id}", "Author", [new(new CalibreAuthorId(id), "Author", "Author")],
                [], [], $"Author/Book {id}")),
            []);
        LibraryState state = LibraryState.FromScan(snapshot, Generation);
        LibraryStateDelta[] deltas = Enumerable.Range(0, removalCount)
            .Select(index => (LibraryStateDelta)new RemoveRecordLibraryStateDelta(
                Generation, new(index), $"remove:{index + 1}", ScannedAt.AddSeconds(index + 1),
                new(index + 1)))
            .ToArray();

        state = LibraryStateDeltaPolicy.ApplyBatch(state, deltas);

        state.Revision.Should().Be(new LibraryStateRevision(removalCount));
        state.Snapshot.Books.Should().HaveCount(recordCount - removalCount);
        state.Snapshot.Books[0].Id.Should().Be(new CalibreBookId(removalCount + 1));
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
