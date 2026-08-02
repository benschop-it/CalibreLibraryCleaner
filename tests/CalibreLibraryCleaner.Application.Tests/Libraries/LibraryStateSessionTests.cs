using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class LibraryStateSessionTests
{
    private static readonly DateTimeOffset ScannedAt = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly FormatFileFingerprint Fingerprint = new(10, new(new string('a', 64)));

    [Fact]
    public void SessionSerializesRevisionsForOneLibrary()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = session.StartFromScan(snapshot).State!;

        LibraryStateSessionOutcome outcome = session.Apply(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(0), "remove-format:2:EPUB",
                ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint));

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.Revision.Should().Be(new LibraryStateRevision(1));
        session.GetCurrent(snapshot.Identity.LibraryRoot).Should().BeSameAs(outcome.State);
    }

    [Fact]
    public void RejectedDeltaDoesNotReplaceCurrentState()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = session.StartFromScan(snapshot).State!;

        LibraryStateSessionOutcome outcome = session.Apply(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(9), "remove-format:2:EPUB",
                ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint));

        outcome.IsSuccess.Should().BeFalse();
        outcome.ErrorCode.Should().Be("LIBRARY_STATE.DELTA_REJECTED");
        session.GetCurrent(snapshot.Identity.LibraryRoot).Should().BeSameAs(baseline);
    }

    [Fact]
    public void UncertaintyPersistsAndBlocksLaterDelta()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = session.StartFromScan(snapshot).State!;
        session.MarkUncertain(snapshot.Identity.LibraryRoot, new(
            "COMMAND_OUTCOME_AMBIGUOUS", "The command result was ambiguous.", ScannedAt.AddSeconds(1)));

        LibraryStateSessionOutcome outcome = session.Apply(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(0), "remove-format:2:EPUB",
                ScannedAt.AddSeconds(2), new(2), "EPUB", Fingerprint));

        outcome.IsSuccess.Should().BeFalse();
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
    }

    [Fact]
    public void DifferentLibrariesKeepIndependentRevisions()
    {
        LibraryStateSession session = new();
        LibrarySnapshot first = Snapshot("C:\\first");
        LibrarySnapshot second = Snapshot("C:\\second");
        LibraryState firstState = session.StartFromScan(first).State!;
        session.StartFromScan(second);

        session.Apply(first.Identity.LibraryRoot, new RemoveFormatLibraryStateDelta(
            firstState.GenerationId, new(0), "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint));

        session.GetCurrent(first.Identity.LibraryRoot)!.Revision.Should().Be(new LibraryStateRevision(1));
        session.GetCurrent(second.Identity.LibraryRoot)!.Revision.Should().Be(new LibraryStateRevision(0));
    }

    private static LibrarySnapshot Snapshot(string root)
    {
        CalibreBook[] books = [Book(1), Book(2)];
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, root),
            ScannedAt,
            books,
            [],
            ExactBinaryDuplicateDetector.Detect(books));
    }

    private static CalibreBook Book(long id)
    {
        string directory = $"Author/Book ({id})";
        return new(
            new(id), "Book", "Author", [new(new(id), "Author", "Author")], [],
            [new("EPUB", "book", $"{directory}/book.epub", FormatFileStatus.Present,
                Fingerprint, new(Fingerprint.SizeInBytes, ScannedAt, ScannedAt, 0))],
            directory);
    }
}
