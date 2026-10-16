using System.Globalization;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Duplicates;

public sealed class MetadataTextNormalizerTests
{
    [Theory]
    [InlineData("Morgan Vale (pseud. van Alex Writer)", "Morgan Vale")]
    [InlineData("Morgan [pseud.] Vale", "Morgan Vale")]
    [InlineData("Vale, Morgan pseud. van Alex Writer", "Vale, Morgan")]
    [InlineData("Morgan Vale is pseud", "Morgan Vale")]
    public void PseudonymAnnotationsMatchCleanAuthorsInBothIdentityPaths(string annotated, string clean)
    {
        MetadataTextNormalizer.TryCreateAuthorSet([annotated, "Second Writer"], out NormalizedAuthorSet? first);
        MetadataTextNormalizer.TryCreateAuthorSet([clean, "Second Writer"], out NormalizedAuthorSet? second);
        MetadataTextNormalizer.TryCreateAuthorSet([clean], out NormalizedAuthorSet? missingCoauthor);

        first.Should().Be(second).And.NotBe(missingCoauthor);
        CandidateMetadataNormalizer.AuthorKeys([annotated]).Should()
            .Equal(CandidateMetadataNormalizer.AuthorKeys([clean]));
        CandidateMetadataNormalizer.AuthorTokens([annotated]).Should()
            .Equal(CandidateMetadataNormalizer.AuthorTokens([clean]));
    }

    [Theory]
    [InlineData("Morgan Vale (pseud. van Alex Writer)", "Alex Writer")]
    [InlineData("Morgan Vale (pseud. van Alex Writer)", "Taylor Reed (pseud. van Alex Writer)")]
    [InlineData("Morgan Vale (editor)", "Morgan Vale")]
    public void PseudonymNormalizationDoesNotMergeRealNamesOtherPenNamesOrUnrelatedRoles(string firstValue, string secondValue)
    {
        MetadataTextNormalizer.TryCreateAuthorSet([firstValue], out NormalizedAuthorSet? first);
        MetadataTextNormalizer.TryCreateAuthorSet([secondValue], out NormalizedAuthorSet? second);

        first.Should().NotBe(second);
        CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(
            CandidateMetadataNormalizer.AuthorKeys([firstValue]),
            CandidateMetadataNormalizer.AuthorKeys([secondValue])).Should().BeFalse();
    }

    [Fact]
    public void PseudonymNormalizationFindsMetadataGroupWithoutChangingOriginalAuthorsOrTitles()
    {
        const string annotated = "Morgan Vale (pseud. van Alex Writer)";
        CalibreBook first = new(new(1), "The Book", "Vale, Morgan",
            [new(new(1), annotated, "Vale, Morgan")], [], [], "Author/Book (1)");
        CalibreBook second = new(new(2), "The Book", "Vale, Morgan",
            [new(new(2), "Morgan Vale", "Vale, Morgan")], [], [], "Author/Book (2)");

        ExactMetadataDuplicateDetector.Detect([first, second]).Should().ContainSingle();
        first.Authors.Single().Name.Should().Be(annotated);
        MetadataTextNormalizer.TryNormalizeTitle(annotated, out NormalizedTitle? title);
        MetadataTextNormalizer.TryNormalizeTitle("Morgan Vale", out NormalizedTitle? cleanTitle);
        title.Should().NotBe(cleanTitle);
    }

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
