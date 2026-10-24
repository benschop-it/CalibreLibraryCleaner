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
        book.Formats.Single().StoredFileName.Should().Be("Final Title - Final Author",
            "catalog stored names are stems, not filenames with an extension");
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

    [Fact]
    public void FreshWorkerObservationMaterializesProjectedFormatsWithoutInventingFileIdentity()
    {
        LibraryState state = State();
        state = LibraryStateDeltaPolicy.Apply(state, new AddOrReplaceFormatLibraryStateDelta(
            state.GenerationId, state.Revision, "project-format", DateTimeOffset.UnixEpoch,
            new(1), "EPUB", Fingerprint, Fingerprint));
        FormatFileObservation current = new(10, DateTimeOffset.UnixEpoch.AddDays(1),
            DateTimeOffset.UnixEpoch.AddDays(2), 32);
        RefreshManagedNameLibraryStateDelta delta = new(
            state.GenerationId, state.Revision, "refresh-observed", DateTimeOffset.UnixEpoch,
            new(1), "Final Author/Final Title (1)",
            [new("EPUB", "Final Title - Final Author.epub", Fingerprint, current)]);

        LibraryState projected = LibraryStateDeltaPolicy.Apply(state, delta);

        BookFormat format = projected.Snapshot.Books.Single().Formats.Single();
        format.FileStatus.Should().Be(FormatFileStatus.Present);
        format.Observation.Should().Be(current);
        format.StoredFileName.Should().Be("Final Title - Final Author");
        format.ExpectedRelativePath.Should().EndWith(".epub").And.NotEndWith(".epub.epub");
    }

    [Fact]
    public void ReviewedTitleWriteProjectsItsLiveRegisteredNamesBeforeAnotherNamingPreflight()
    {
        LibraryState state = State();
        FormatFileObservation current = new(10, DateTimeOffset.UnixEpoch.AddDays(1),
            DateTimeOffset.UnixEpoch.AddDays(2), 32);
        SetMetadataLibraryStateDelta title = new(
            state.GenerationId, state.Revision, "reviewed-title", DateTimeOffset.UnixEpoch,
            new(1), LibraryMetadataField.Title, ["Reviewed Title"],
            verifiedManagedPath: "Final Author/Reviewed Title (1)",
            verifiedFormats: [new("EPUB", "Reviewed Title - Final Author.epub", Fingerprint, current)]);

        LibraryState projected = LibraryStateDeltaPolicy.Apply(state, title);

        CalibreBook book = projected.Snapshot.Books.Single();
        book.Title.Should().Be("Reviewed Title");
        book.Formats.Single().StoredFileName.Should().Be("Reviewed Title - Final Author");
        book.Formats.Single().ExpectedRelativePath.Should().Be(
            "Final Author/Reviewed Title (1)/Reviewed Title - Final Author.epub");
        book.Formats.Single().Observation.Should().Be(current);
        book.Formats.Single().Fingerprint.Should().Be(Fingerprint);
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
