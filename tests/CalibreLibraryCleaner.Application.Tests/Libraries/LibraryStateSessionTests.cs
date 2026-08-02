using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
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
    public async Task SessionSerializesRevisionsForOneLibrary()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(0), "remove-format:2:EPUB",
            ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.State!.Revision.Should().Be(new LibraryStateRevision(1));
        session.GetCurrent(snapshot.Identity.LibraryRoot).Should().BeSameAs(outcome.State);
    }

    [Fact]
    public async Task RejectedDeltaMarksStateUncertain()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(9), "remove-format:2:EPUB",
            ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.ErrorCode.Should().Be("LIBRARY_STATE.DELTA_REJECTED");
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
    }

    [Fact]
    public async Task UncertaintyPersistsAndBlocksLaterDelta()
    {
        LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;
        await session.MarkUncertainAsync(snapshot.Identity.LibraryRoot, new(
            "COMMAND_OUTCOME_AMBIGUOUS", "The command result was ambiguous.", ScannedAt.AddSeconds(1)),
            CancellationToken.None);

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(0), "remove-format:2:EPUB",
                ScannedAt.AddSeconds(2), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
    }

    [Fact]
    public async Task DifferentLibrariesKeepIndependentRevisions()
    {
        LibraryStateSession session = new();
        LibrarySnapshot first = Snapshot("C:\\first");
        LibrarySnapshot second = Snapshot("C:\\second");
        LibraryState firstState = (await session.StartFromScanAsync(first, CancellationToken.None)).State!;
        await session.StartFromScanAsync(second, CancellationToken.None);

        await session.ApplyAsync(first.Identity.LibraryRoot, new RemoveFormatLibraryStateDelta(
            firstState.GenerationId, new(0), "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint),
            CancellationToken.None);

        session.GetCurrent(first.Identity.LibraryRoot)!.Revision.Should().Be(new LibraryStateRevision(1));
        session.GetCurrent(second.Identity.LibraryRoot)!.Revision.Should().Be(new LibraryStateRevision(0));
    }

    [Fact]
    public async Task SuccessfulDeltaPublishesCommittedRevision()
    {
        using LibraryStateSession session = new();
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;
        List<LibraryState> published = [];
        session.StateChanged += (_, eventArgs) => published.Add(eventArgs.State);

        await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, baseline.Revision,
                "remove-format:2:EPUB", ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint),
            CancellationToken.None);

        published.Should().ContainSingle().Which.Revision.Should().Be(new LibraryStateRevision(1));
    }

    [Fact]
    public async Task UncertaintyPersistenceFailureIsReportedAndRemainsBlockedInMemory()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        A.CallTo(() => store.WriteUncertaintyAsync(A<string>._, A<LibraryState>._,
                A<CancellationToken>._))
            .ThrowsAsync(new IOException("controlled persistence failure"));
        using LibraryStateSession session = new(store);
        LibrarySnapshot snapshot = Snapshot("C:\\library");
        LibraryState baseline = (await session.StartFromScanAsync(snapshot, CancellationToken.None)).State!;

        LibraryStateSessionOutcome outcome = await session.ApplyAsync(snapshot.Identity.LibraryRoot,
            new RemoveFormatLibraryStateDelta(baseline.GenerationId, new(9), "stale-delta",
                ScannedAt.AddSeconds(1), new(2), "EPUB", Fingerprint), CancellationToken.None);

        outcome.ErrorCode.Should().Be("LIBRARY_STATE.UNCERTAINTY_PERSIST_FAILED");
        session.GetCurrent(snapshot.Identity.LibraryRoot)!.Status.Should().Be(LibraryStateStatus.Uncertain);
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
