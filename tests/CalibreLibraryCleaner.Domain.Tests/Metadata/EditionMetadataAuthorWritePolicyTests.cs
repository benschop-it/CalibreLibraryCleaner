using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class EditionMetadataAuthorWritePolicyTests
{
    [Theory]
    [InlineData("A.C. Baantjer", "A. C. Baantjer")]
    [InlineData("A. C. Baantjer", "A. C. Baantjer")]
    [InlineData("Baantjer, A.C.", "A. C. Baantjer")]
    [InlineData("Baantjer| A.C.", "A. C. Baantjer")]
    [InlineData("J.K. Example", "J. K. Example")]
    [InlineData("A.F.Th. van der Example", "A. F. Th. van der Example")]
    [InlineData("И.П. Пример", "И. П. Пример")]
    public void PreparedDisplayNamesKeepCanonicalDotAndSpaceInitials(string provider, string expected)
    {
        EditionMetadataAuthorWritePolicy.TryPrepare([provider], out IReadOnlyList<string> authors)
            .Should().BeTrue();

        authors.Should().Equal(expected);
        EditionMetadataAuthorWritePolicy.TryPrepare(authors, out IReadOnlyList<string> repeated).Should().BeTrue();
        repeated.Should().Equal(authors);
    }

    [Fact]
    public void InitialTypographyCannotSilentlyCollapseTwoProviderCoauthors()
    {
        EditionMetadataAuthorWritePolicy.TryPrepare(
            ["A.C. Writer", "A. C. Writer"], out IReadOnlyList<string> authors).Should().BeFalse();

        authors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Henry, Patrick", "Patrick Henry")]
    [InlineData("Henry| Patrick", "Patrick Henry")]
    [InlineData("Jong, Lou de", "Lou de Jong")]
    [InlineData("Jong| Lou de", "Lou de Jong")]
    [InlineData("García Márquez, Gabriel", "Gabriel García Márquez")]
    [InlineData("王| 小明", "小明 王")]
    public void IndividualExplicitFamilyGivenNamesBecomeDisplayNames(string provider, string expected)
    {
        EditionMetadataAuthorWritePolicy.TryPrepare([provider], out IReadOnlyList<string> authors)
            .Should().BeTrue();

        authors.Should().Equal(expected);
        EditionMetadataAuthorWritePolicy.TryPrepare(authors, out IReadOnlyList<string> repeated).Should().BeTrue();
        repeated.Should().Equal(authors);
    }

    [Fact]
    public void MixedExplicitCreditsPreserveTheOrderedCompleteAuthorList()
    {
        EditionMetadataAuthorWritePolicy.TryPrepare(
            ["Henry, Patrick", "First Writer", "Family| Second"], out IReadOnlyList<string> authors)
            .Should().BeTrue();

        authors.Should().Equal("Patrick Henry", "First Writer", "Second Family");
    }

    [Theory]
    [InlineData("Lannert| Stacy| Kemp| Kristen")]
    [InlineData("Lannert, Stacy & Kemp, Kristen")]
    public void MultipleExplicitFamilyGivenPairsBecomeSeparateOrderedAuthors(string provider)
    {
        EditionMetadataAuthorWritePolicy.TryPrepare([provider], out IReadOnlyList<string> authors)
            .Should().BeTrue();

        authors.Should().Equal("Stacy Lannert", "Kristen Kemp");
        EditionMetadataAuthorWritePolicy.TryPrepare(authors, out IReadOnlyList<string> repeated).Should().BeTrue();
        repeated.Should().Equal(authors);
    }

    [Fact]
    public void SeparateProviderCommaCreditsUseDisplayOrderForEveryAuthor()
    {
        EditionMetadataAuthorWritePolicy.TryPrepare(
            ["Lannert, Stacy", "Kemp, Kristen"], out IReadOnlyList<string> authors).Should().BeTrue();

        authors.Should().Equal("Stacy Lannert", "Kristen Kemp");
    }

    [Fact]
    public void PipePairCountIsBoundedAndAnIncompleteExtraCreditCannotPartiallyApply()
    {
        string[] parts = Enumerable.Range(0, 8)
            .SelectMany(index => new[] { $"Family {(char)('A' + index)}", $"Given {(char)('A' + index)}" }).ToArray();
        EditionMetadataAuthorWritePolicy.TryPrepare(
            [string.Join('|', parts)], out IReadOnlyList<string> authors).Should().BeTrue();
        authors.Should().HaveCount(8);

        EditionMetadataAuthorWritePolicy.TryPrepare(
            [string.Join('|', parts.Append("Extra Family"))], out IReadOnlyList<string> incomplete)
            .Should().BeFalse();
        incomplete.Should().BeEmpty();
        EditionMetadataAuthorWritePolicy.TryPrepare(
            [string.Join('|', parts.Concat(["Extra Family", "Extra Given"]))], out IReadOnlyList<string> overLimit)
            .Should().BeFalse();
        overLimit.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Family, Given, Jr.")]
    [InlineData("Family| Given| 1900-1999")]
    [InlineData("Family| Given, Other")]
    [InlineData("Family|| Given")]
    [InlineData("Family,")]
    [InlineData("| Given")]
    [InlineData("Family, Given (editor)")]
    [InlineData("Family| Given| Other")]
    [InlineData("Family| Given| Other|")]
    [InlineData("Family| Given| Qualifier| 1900-1999")]
    public void AmbiguousDelimitedProviderNamesAreWithheldWithoutDroppingQualifiers(string provider)
    {
        EditionMetadataAuthorWritePolicy.TryPrepare([provider], out IReadOnlyList<string> authors)
            .Should().BeFalse();

        authors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Victor [pseud. ] Victor [pseud.] Appleton")]
    [InlineData("Christer, Sam psued. van Michael Morley")]
    [InlineData("Morgan Vale (pseud. van Another Writer)")]
    [InlineData("A. Writer [pseudoniem]")]
    [InlineData("A. Writer (pseudonym")]
    [InlineData("Family, Given psued. van Other Writer")]
    public void ProviderPseudonymAnnotationsCannotBecomeWritableAuthors(string value)
    {
        EditionMetadataAuthorWritePolicy.TryPrepare([value], out IReadOnlyList<string> authors)
            .Should().BeFalse("provider annotations must not undo reviewed author cleanup");

        authors.Should().BeEmpty();
    }

    [Fact]
    public void WithheldAnnotatedCreditCannotDropOrPartiallyReplaceOtherCoauthors()
    {
        EditionMetadataAuthorWritePolicy.TryPrepare(
            ["Clean Coauthor", "Family, Given pseud. van Another Writer"], out IReadOnlyList<string> authors)
            .Should().BeFalse();

        authors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Pseudoscience Research Group")]
    [InlineData("Pseudonymous Press")]
    [InlineData("Pseudora Vale")]
    [InlineData("Anonymous")]
    [InlineData("Sam Christer")]
    public void LegitimateUnannotatedCreditsRemainWritable(string value)
    {
        EditionMetadataAuthorWritePolicy.TryPrepare([value], out IReadOnlyList<string> authors)
            .Should().BeTrue();

        authors.Should().Equal(value);
    }

    [Fact]
    public void StrictCombinedCommaAuthorsBecomeSeparateDisplayAuthors()
    {
        EditionMetadataAuthorWritePolicy.TryPrepare(
                ["Pohl, Frederik & Williamson, Jack"],
                out IReadOnlyList<string> result)
            .Should().BeTrue();

        result.Should().Equal("Frederik Pohl", "Jack Williamson");
    }

    [Theory]
    [InlineData("Pohl, Frederik & Williamson")]
    [InlineData("Research & Development")]
    [InlineData("Family, Given & Other, Given, Suffix")]
    public void AmbiguousCombinedAuthorsAreRejected(string value)
    {
        EditionMetadataAuthorWritePolicy.TryPrepare([value], out _).Should().BeFalse();
    }

    [Fact]
    public void OrdinaryAuthorListsPreserveOrderAndSpelling()
    {
        EditionMetadataAuthorWritePolicy.TryPrepare(
                ["Jane Austen", "Gabriel García Márquez"],
                out IReadOnlyList<string> result)
            .Should().BeTrue();

        result.Should().Equal("Jane Austen", "Gabriel García Márquez");
    }
}
