using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class LibraryValueTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BookIdRejectsNonPositiveValues(long value)
    {
        Action act = () => _ = new CalibreBookId(value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BookFormatRetainsMissingStatus()
    {
        BookFormat format = new("EPUB", "Example", "Author/Example/Example.epub", FormatFileStatus.Missing);

        format.FileStatus.Should().Be(FormatFileStatus.Missing);
    }

    [Fact]
    public void Sha256DigestNormalizesHexadecimalText()
    {
        Sha256Digest digest = new(new string('A', 64));

        digest.Value.Should().Be(new string('a', 64));
    }

    [Fact]
    public void PresentFormatRequiresFingerprint()
    {
        Action act = () => _ = new BookFormat("EPUB", "Book", "Book.epub", FormatFileStatus.Present);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PublicationMetadataPreservesStoredTextAndLanguageOrder()
    {
        BookPublicationMetadata metadata = new(
            publisher: "  Publisher  ",
            series: "  Series  ",
            languages: ["eng", " ENG ", "deu"]);

        metadata.Publisher.Should().Be("  Publisher  ");
        metadata.Series.Should().Be("  Series  ");
        metadata.Languages.Should().Equal("eng", " ENG ", "deu");
    }
}
