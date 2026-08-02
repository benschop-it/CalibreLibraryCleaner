using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Duplicates;

public sealed class ExactBinaryRetentionPolicyTests
{
    private static readonly FormatFileFingerprint Shared = new(10, new Sha256Digest(new string('a', 64)));

    [Fact]
    public void RecordWithMoreFormatsWinsInsteadOfFirstMember()
    {
        CalibreBook first = Book(1, [Format("EPUB", Shared)]);
        CalibreBook richer = Book(2, [Format("EPUB", Shared), Format("PDF", Fingerprint('b'))]);
        ExactBinaryDuplicateGroup group = ExactBinaryDuplicateDetector.Detect([first, richer]).Single();

        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy.Select([group], [first, richer]).Single();

        decision.RetainedMember!.BookId.Should().Be(new CalibreBookId(2));
        decision.FormatRemovals.Select(value => value.BookId).Should().Equal(new CalibreBookId(1));
    }

    [Fact]
    public void FormatCountRanksBeforeBetterMetadata()
    {
        CalibreBook complete = Book(1, [Format("EPUB", Shared)],
            title: "Complete title",
            identifiers: [new("ISBN", "9780306406157")],
            publication: new("Publisher", DateTimeOffset.UnixEpoch, "Series", 1, ["eng"], true));
        CalibreBook moreFormats = new(
            new(2), "Unknown", "Unknown", [new(new(2), "Unknown", "Unknown")], [],
            [Format("EPUB", Shared), Format("PDF", Fingerprint('b'))], "Author/Book 2");
        ExactBinaryDuplicateGroup group = ExactBinaryDuplicateDetector.Detect([complete, moreFormats]).Single();

        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy.Select(
            [group], [complete, moreFormats]).Single();

        decision.RetainedMember!.BookId.Should().Be(new CalibreBookId(2));
    }

    [Fact]
    public void RecordParticipatingInMultipleGroupsIsRankedConsistently()
    {
        FormatFileFingerprint epubFingerprint = Shared;
        FormatFileFingerprint pdfFingerprint = Fingerprint('b');
        CalibreBook multiFormat = Book(3,
            [Format("EPUB", epubFingerprint), Format("PDF", pdfFingerprint)]);
        CalibreBook epubOnly = Book(1, [Format("EPUB", epubFingerprint)]);
        CalibreBook pdfOnly = Book(2, [Format("PDF", pdfFingerprint)]);
        CalibreBook[] books = [epubOnly, pdfOnly, multiFormat];
        IReadOnlyList<ExactBinaryDuplicateGroup> groups = ExactBinaryDuplicateDetector.Detect(books);

        IReadOnlyList<ExactBinaryRetentionDecision> decisions = ExactBinaryRetentionPolicy.Select(groups, books);

        decisions.Should().HaveCount(2);
        decisions.Should().OnlyContain(value => value.RetainedMember!.BookId == new CalibreBookId(3));
    }

    [Fact]
    public void MetadataIdentifiersAndCoverRankBeforeRecordId()
    {
        CalibreBook first = Book(1, [Format("EPUB", Shared)]);
        CalibreBook better = Book(2, [Format("EPUB", Shared)],
            title: "Complete title",
            identifiers: [new("ISBN", "9780306406157")],
            publication: new("Publisher", DateTimeOffset.UnixEpoch, "Series", 1, ["eng"], true));
        ExactBinaryDuplicateGroup group = ExactBinaryDuplicateDetector.Detect([first, better]).Single();

        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy.Select([group], [first, better]).Single();

        decision.RetainedMember!.BookId.Should().Be(new CalibreBookId(2));
        decision.Candidates[0].ValidStrongIdentifierCount.Should().Be(1);
        decision.Candidates[0].HasCover.Should().BeTrue();
    }

    [Fact]
    public void LowestRecordIdIsOnlyFinalTieBreaker()
    {
        CalibreBook later = Book(8, [Format("EPUB", Shared)]);
        CalibreBook earlier = Book(3, [Format("EPUB", Shared)]);
        ExactBinaryDuplicateGroup group = ExactBinaryDuplicateDetector.Detect([later, earlier]).Single();

        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy.Select([group], [later, earlier]).Single();

        decision.RetainedMember!.BookId.Should().Be(new CalibreBookId(3));
    }

    [Fact]
    public void MixedCanonicalFormatsAreSkipped()
    {
        CalibreBook epub = Book(1, [Format("EPUB", Shared)]);
        CalibreBook pdf = Book(2, [Format("PDF", Shared)]);
        ExactBinaryDuplicateGroup group = ExactBinaryDuplicateDetector.Detect([epub, pdf]).Single();

        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy.Select([group], [epub, pdf]).Single();

        decision.IsEligible.Should().BeFalse();
        decision.RetainedMember.Should().BeNull();
        decision.FormatRemovals.Should().BeEmpty();
        decision.SkipReason.Should().Contain("different canonical format labels");
    }

    private static CalibreBook Book(
        long id,
        IEnumerable<BookFormat> formats,
        string? title = null,
        IEnumerable<BookIdentifier>? identifiers = null,
        BookPublicationMetadata? publication = null) => new(
        new(id),
        title ?? $"Book {id}",
        "Author",
        [new(new(id), "Author", "Author")],
        identifiers ?? [],
        formats,
        $"Author/Book {id}",
        publication);

    private static BookFormat Format(string format, FormatFileFingerprint fingerprint) => new(
        format,
        "Book",
        $"Author/Book/Book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        fingerprint,
        new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

    private static FormatFileFingerprint Fingerprint(char value) => new(10, new Sha256Digest(new string(value, 64)));
}
