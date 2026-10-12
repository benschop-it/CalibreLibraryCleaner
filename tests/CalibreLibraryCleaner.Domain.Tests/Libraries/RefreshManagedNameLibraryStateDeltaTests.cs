using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class RefreshManagedNameLibraryStateDeltaTests
{
    private static readonly FormatFileFingerprint Fingerprint = new(
        10, new(new string('a', 64)));
    private static readonly FormatFileObservation Observation = new(
        10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);

    [Fact]
    public void VerifiedReadBackProjectsActualPathAndBasenameWithoutChangingIdentity()
    {
        LibraryState state = State();
        RefreshManagedNameLibraryStateDelta delta = new(
            state.GenerationId,
            state.Revision,
            "refresh-name",
            DateTimeOffset.UnixEpoch,
            new(1),
            "Final Author/Final Title (1)",
            [new("EPUB", "Final Title - Final Author.epub", Fingerprint)]);

        LibraryState projected = LibraryStateDeltaPolicy.Apply(state, delta);

        CalibreBook book = projected.Snapshot.Books.Single();
        book.RelativeDirectory.Should().Be("Final Author/Final Title (1)");
        book.Formats.Single().StoredFileName.Should().Be("Final Title - Final Author.epub");
        book.Formats.Single().ExpectedRelativePath.Should().Be(
            "Final Author/Final Title (1)/Final Title - Final Author.epub");
        book.Formats.Single().Fingerprint.Should().Be(Fingerprint);
        book.Formats.Single().Observation.Should().Be(Observation);
    }

    [Theory]
    [InlineData("Author/Title")]
    [InlineData("Author/Title (2)")]
    [InlineData("Extra/Author/Title (1)")]
    public void InvalidDepthOrRecordSuffixIsRejected(string path)
    {
        LibraryState state = State();

        Action act = () => _ = new RefreshManagedNameLibraryStateDelta(
            state.GenerationId, state.Revision, "refresh", DateTimeOffset.UnixEpoch,
            new(1), path, [new("EPUB", "book.epub", Fingerprint)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChangedFormatFingerprintStopsProjection()
    {
        LibraryState state = State();
        RefreshManagedNameLibraryStateDelta delta = new(
            state.GenerationId, state.Revision, "refresh", DateTimeOffset.UnixEpoch,
            new(1), "Author/Title (1)",
            [new("EPUB", "book.epub", new(10, new(new string('b', 64))))]);

        Action act = () => LibraryStateDeltaPolicy.Apply(state, delta);

        act.Should().Throw<InvalidOperationException>().WithMessage("*format set or bytes*");
    }

    private static LibraryState State()
    {
        CalibreBook book = new(
            new(1), "Final Title", "Final Author", [new(new(1), "Final Author", "Final Author")],
            [],
            [new(
                "EPUB", "old.epub", "Old Author/Old Title (1)/old.epub",
                FormatFileStatus.Present, Fingerprint, Observation)],
            "Old Author/Old Title (1)");
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\library"), DateTimeOffset.UnixEpoch, [book], []);
        return LibraryState.FromScan(
            snapshot, new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
    }
}
