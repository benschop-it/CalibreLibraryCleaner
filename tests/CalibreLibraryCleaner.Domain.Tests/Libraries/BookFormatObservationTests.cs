using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class BookFormatObservationTests
{
    [Fact]
    public void PresentFormatRequiresMatchingFingerprintAndVerifiedObservation()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));

        Action missing = () => _ = new BookFormat("EPUB", "book", "book.epub", FormatFileStatus.Present, fingerprint);
        Action mismatched = () => _ = new BookFormat("EPUB", "book", "book.epub", FormatFileStatus.Present, fingerprint,
            new(6, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

        missing.Should().Throw<ArgumentException>();
        mismatched.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NonPresentFormatRejectsFingerprintOrObservation()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));
        Action action = () => _ = new BookFormat("EPUB", "book", "book.epub", FormatFileStatus.Missing, fingerprint,
            new(5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CleanupPlanExpectedFormatRejectsPathShapedStoredBasename()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));
        FormatFileObservation observation = new(5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);

        Action action = () => _ = new ExpectedFormatState(new(1), "EPUB", "C:\\private\\book",
            "Author/Book (1)/book.epub", FormatFileStatus.Present, fingerprint, observation);

        action.Should().Throw<ArgumentException>();
    }
}
