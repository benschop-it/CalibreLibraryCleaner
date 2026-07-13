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
}
