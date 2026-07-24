using System.Globalization;
using CalibreLibraryCleaner.Domain.Duplicates;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Duplicates;

public sealed class MetadataTextNormalizerTests
{
    [Fact]
    public void TitleNormalizationUsesNfcInvariantCaseWhitespaceAndPunctuationSpacing()
    {
        bool firstSuccess = MetadataTextNormalizer.TryNormalizeTitle(
            "  Cafe\u0301\t:  A\u00a0Tale\u200b  ",
            out NormalizedTitle? first);
        bool secondSuccess = MetadataTextNormalizer.TryNormalizeTitle(
            "CAFÉ:A TALE",
            out NormalizedTitle? second);

        firstSuccess.Should().BeTrue();
        secondSuccess.Should().BeTrue();
        first.Should().Be(second);
        first!.Value.Should().Be("CAFÉ:A TALE");
    }

    [Fact]
    public void CompatibilityCharactersRemainDistinct()
    {
        MetadataTextNormalizer.TryNormalizeTitle("ABC", out NormalizedTitle? ascii);
        MetadataTextNormalizer.TryNormalizeTitle("ＡＢＣ", out NormalizedTitle? fullWidth);

        ascii.Should().NotBe(fullWidth);
    }

    [Theory]
    [InlineData("The Book: A Tale", "The Book")]
    [InlineData("J. R. R. Tolkien", "J R R Tolkien")]
    [InlineData("J. R. R. Tolkien", "JRR Tolkien")]
    [InlineData("Doe, Jane", "Jane Doe")]
    [InlineData("Book-One", "Book—One")]
    [InlineData("The Book", "Book")]
    [InlineData("Book (Second Edition)", "Book")]
    [InlineData("Book [Illustrated]", "Book")]
    public void SignificantStoredTextRemainsDistinct(string firstValue, string secondValue)
    {
        MetadataTextNormalizer.TryNormalizeTitle(firstValue, out NormalizedTitle? first);
        MetadataTextNormalizer.TryNormalizeTitle(secondValue, out NormalizedTitle? second);

        first.Should().NotBe(second);
    }

    [Theory]
    [InlineData("Doe, Jane", "Jane Doe")]
    [InlineData("J. R. R. Tolkien", "J R R Tolkien")]
    [InlineData("J. R. R. Tolkien", "JRR Tolkien")]
    public void SignificantAuthorTextRemainsDistinct(string firstValue, string secondValue)
    {
        MetadataTextNormalizer.TryCreateAuthorSet([firstValue], out NormalizedAuthorSet? first);
        MetadataTextNormalizer.TryCreateAuthorSet([secondValue], out NormalizedAuthorSet? second);

        first.Should().NotBe(second);
    }

    [Theory]
    [InlineData("A _ B", "A_B")]
    [InlineData("A - B", "A-B")]
    [InlineData("A ( B )", "A(B)")]
    [InlineData("A \u2018 B \u2019", "A\u2018B\u2019")]
    [InlineData("A ; B", "A;B")]
    [InlineData("Book ? !", "BOOK?!")]
    public void PunctuationCategoriesAndRepetitionArePreservedWhileSpacingIsNormalized(
        string value,
        string expected)
    {
        MetadataTextNormalizer.TryNormalizeTitle(value, out NormalizedTitle? title).Should().BeTrue();

        title!.Value.Should().Be(expected);
    }

    [Fact]
    public void InvariantCaseDoesNotDependOnCurrentCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            MetadataTextNormalizer.TryNormalizeAuthorName("ili", out NormalizedAuthorName? turkish);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            MetadataTextNormalizer.TryNormalizeAuthorName("ili", out NormalizedAuthorName? english);

            turkish.Should().Be(english);
            turkish!.Value.Should().Be("ILI");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void AuthorSetIsOrderIndependentAndDeduplicated()
    {
        MetadataTextNormalizer.TryCreateAuthorSet(
            ["Bob Jones", "Alice Smith", "alice  smith"],
            out NormalizedAuthorSet? first);
        MetadataTextNormalizer.TryCreateAuthorSet(
            ["Alice Smith", "Bob Jones"],
            out NormalizedAuthorSet? second);

        first.Should().Be(second);
        first!.Names.Select(name => name.Value).Should().Equal("ALICE SMITH", "BOB JONES");
    }

    [Fact]
    public void AnyUnusableAuthorMakesTheEntireAuthorSetIneligible()
    {
        bool success = MetadataTextNormalizer.TryCreateAuthorSet(
            ["Jane Doe", "\u200b"],
            out NormalizedAuthorSet? authors);

        success.Should().BeFalse();
        authors.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(UnusableValues))]
    public void EmptyPostNormalizationValueIsRejected(string value)
    {
        MetadataTextNormalizer.TryNormalizeTitle(value, out NormalizedTitle? title).Should().BeFalse();
        title.Should().BeNull();
    }

    [Fact]
    public void LiteralUnknownIsOrdinaryStoredText()
    {
        MetadataTextNormalizer.TryNormalizeAuthorName("Unknown", out NormalizedAuthorName? author);

        author!.Value.Should().Be("UNKNOWN");
    }

    [Fact]
    public void MalformedUtf16IsRejected()
    {
        string malformed = new(['\ud800']);

        MetadataTextNormalizer.TryNormalizeTitle(malformed, out NormalizedTitle? title).Should().BeFalse();
        title.Should().BeNull();
    }

    public static TheoryData<string> UnusableValues => new()
    {
        "   ",
        "\u200b\u200e",
    };
}
