using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class PipeAuthorNameNormalizationPolicyTests
{
    [Theory]
    [InlineData("Woods| Stuart", "Stuart Woods", "Woods, Stuart")]
    [InlineData("Jong| Lou de", "Lou de Jong", "Jong, Lou de")]
    [InlineData("Adama van Scheltema| Carel Steven", "Carel Steven Adama van Scheltema", "Adama van Scheltema, Carel Steven")]
    [InlineData("Kipling| Rudyard| 1865-1936", "Rudyard Kipling", "Kipling, Rudyard")]
    [InlineData("Erickson| Carolly| 1943-", "Carolly Erickson", "Erickson, Carolly")]
    [InlineData("Kipling| Rudyard|", "Rudyard Kipling", "Kipling, Rudyard")]
    public void OnePipePreservesExplicitFamilyAndGivenParts(
        string value,
        string displayName,
        string sortName)
    {
        PipeAuthorNameNormalizationPolicy.TryNormalize(value, out PipeAuthorNameNormalization? result)
            .Should().BeTrue();

        result.Should().Be(new PipeAuthorNameNormalization(displayName, sortName));
    }

    [Theory]
    [InlineData("(1940) One| Two| Buckle My Shoe")]
    [InlineData("Family|")]
    [InlineData("| Given")]
    [InlineData("Davis| Richard| Jr.")]
    [InlineData("Tolstoy| Leo| graf| 1828-1910")]
    [InlineData("Family| Given| 19-20")]
    [InlineData("Firstname Lastname")]
    public void AmbiguousOrNonPipeNamesRemainUnchanged(string value)
    {
        PipeAuthorNameNormalizationPolicy.TryNormalize(value, out _).Should().BeFalse();
    }

    [Fact]
    public void WholeBookCommaNamesProduceDisplayAndExactSortLists()
    {
        BookAuthor[] authors =
        [
            new(new(1), "Pohl, Frederik", "Pohl, Frederik"),
            new(new(2), "Williamson, Jack", "Williamson, Jack"),
        ];

        PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
                authors, out IReadOnlyList<string> displays, out IReadOnlyList<string> sorts)
            .Should().BeTrue();

        displays.Should().Equal("Frederik Pohl", "Jack Williamson");
        sorts.Should().Equal("Pohl, Frederik", "Williamson, Jack");
    }

    [Fact]
    public void WholeBookPipeNamesWithMatchingSortsProduceTheSamePlan()
    {
        BookAuthor[] authors =
        [
            new(new(1), "Pohl| Frederik", "Pohl, Frederik"),
            new(new(2), "Williamson| Jack", "Williamson, Jack"),
        ];

        PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
                authors, out IReadOnlyList<string> displays, out IReadOnlyList<string> sorts)
            .Should().BeTrue();

        displays.Should().Equal("Frederik Pohl", "Jack Williamson");
        sorts.Should().Equal("Pohl, Frederik", "Williamson, Jack");
    }

    [Theory]
    [InlineData("Hagen | Aefke ten", "Hagen , Aefke ten", "Aefke ten Hagen", "Hagen, Aefke ten")]
    [InlineData("Kinkel|Tanja", "Kinkel,Tanja", "Tanja Kinkel", "Kinkel, Tanja")]
    public void PipeNamesAcceptEquivalentSortSpacing(
        string name,
        string sort,
        string expectedDisplay,
        string expectedSort)
    {
        PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
                [new(new(1), name, sort)],
                out IReadOnlyList<string> displays,
                out IReadOnlyList<string> sorts)
            .Should().BeTrue();

        displays.Should().Equal(expectedDisplay);
        sorts.Should().Equal(expectedSort);
    }

    [Theory]
    [InlineData("Kipling| Rudyard| 1865-1936", "Kipling, Rudyard, 1865-1936")]
    [InlineData("Erickson| Carolly| 1943-", "Erickson, Carolly, 1943-")]
    [InlineData("Kipling| Rudyard|", "Kipling, Rudyard,")]
    public void PipeDateSuffixRequiresEquivalentSort(
        string name,
        string sort)
    {
        PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
                [new(new(1), name, sort)],
                out IReadOnlyList<string> displays,
                out IReadOnlyList<string> sorts)
            .Should().BeTrue();

        displays.Should().Equal(name.StartsWith("Kipling", StringComparison.Ordinal)
            ? "Rudyard Kipling"
            : "Carolly Erickson");
        sorts.Should().Equal(name.StartsWith("Kipling", StringComparison.Ordinal)
            ? "Kipling, Rudyard"
            : "Erickson, Carolly");
    }

    [Fact]
    public void WholeBookRejectsMixedOrNonmatchingSorts()
    {
        PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
            [new(new(1), "Pohl, Frederik", "Pohl, Frederik"), new(new(2), "Jack Williamson", "Williamson, Jack")],
            out _, out _).Should().BeFalse();
        PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
            [new(new(1), "Pohl, Frederik", "Different, Sort")], out _, out _).Should().BeFalse();
        PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
            [new(new(1), "Pohl| Frederik", "Different, Sort")], out _, out _).Should().BeFalse();
    }
}
