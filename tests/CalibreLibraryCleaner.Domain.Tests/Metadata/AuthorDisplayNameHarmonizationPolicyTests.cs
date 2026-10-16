using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class AuthorDisplayNameHarmonizationPolicyTests
{
    [Theory]
    [InlineData("A van Aardenburg", "Aardenburg, A van", "A. van Aardenburg", "Aardenburg, A. van")]
    [InlineData("A den Doolaard", "Doolaard, A den", "A. den Doolaard", "Doolaard, A. den")]
    [InlineData("J.K. Rowling", "Rowling, J.K.", "J. K. Rowling", "Rowling, J. K.")]
    [InlineData("C.J. Box", "Box, C.J.", "C. J. Box", "Box, C. J.")]
    [InlineData("A.F.Th. van der Heijden", "Heijden, A.F.Th. van der", "A. F. Th. van der Heijden", "Heijden, A. F. Th. van der")]
    [InlineData("P.. de Roos", "Roos, P.. de", "P. de Roos", "Roos, P. de")]
    [InlineData("F .R. Eckmar", "Eckmar, F .R.", "F. R. Eckmar", "Eckmar, F. R.")]
    public void InitialTypographyProducesCanonicalDisplayAndSort(
        string display,
        string sort,
        string expectedDisplay,
        string expectedSort)
    {
        AuthorDisplayNameHarmonizationPolicy.TryHarmonizeBook(
            [Author(display, sort)],
            out IReadOnlyList<string> displays,
            out IReadOnlyList<string> sorts).Should().BeTrue();

        displays.Should().Equal(expectedDisplay);
        sorts.Should().Equal(expectedSort);
    }

    [Theory]
    [InlineData("Peter D' Hamecourt", "Hamecourt, Peter D'", "Peter D'Hamecourt", "Hamecourt, Peter D'")]
    [InlineData("Youp van ’t Hek", "Hek, Youp van ’t", "Youp van 't Hek", "Hek, Youp van 't")]
    [InlineData("Björn Hellberg", "Hellberg, Björn", "Björn Hellberg", "Hellberg, Björn")]
    public void ApostropheAndUnicodeTypographyIsCanonicalized(
        string display,
        string sort,
        string expectedDisplay,
        string expectedSort)
    {
        AuthorDisplayNameHarmonizationPolicy.TryHarmonizeBook(
            [Author(display, sort)],
            out IReadOnlyList<string> displays,
            out IReadOnlyList<string> sorts).Should().BeTrue();

        displays.Should().Equal(expectedDisplay);
        sorts.Should().Equal(expectedSort);
    }

    [Theory]
    [InlineData("P.S. Ik hou van je")]
    [InlineData("I Feel Bad About My Neck (v1.0) [html]")]
    [InlineData("J.R.R. Tolkien (vertaling E.J. Mensink)")]
    [InlineData("Reliable Writer")]
    [InlineData("Youp van 't Hek")]
    public void RiskyOrCanonicalNamesRemainUnchanged(string display)
    {
        AuthorDisplayNameHarmonizationPolicy.TryHarmonizeBook(
            [Author(display, display)], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void HarmonizationCannotCollapseTwoCurrentCoauthors()
    {
        AuthorDisplayNameHarmonizationPolicy.TryHarmonizeBook(
            [Author("J.K. Rowling", "Rowling, J.K."), Author("J. K. Rowling", "Rowling, J. K.")],
            out _, out _).Should().BeFalse();
    }

    private static BookAuthor Author(string display, string sort) => new(null, display, sort);
}
