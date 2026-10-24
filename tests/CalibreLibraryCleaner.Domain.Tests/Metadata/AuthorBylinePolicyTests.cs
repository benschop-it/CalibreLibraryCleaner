using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class AuthorBylinePolicyTests
{
    [Theory]
    [InlineData("By Prince", "Prince")]
    [InlineData("Written by J. K. Rowling", "J. K. Rowling")]
    [InlineData("Author: Ludwig van Beethoven", "Ludwig van Beethoven")]
    [InlineData("By World Health Organization", "World Health Organization")]
    [InlineData("By Gabriel García Márquez", "Gabriel García Márquez")]
    [InlineData("By 村上 春樹", "村上 春樹")]
    [InlineData("Geschreven door A. Schrijver", "A. Schrijver")]
    [InlineData("Auteur: É. Exemple", "É. Exemple")]
    [InlineData("Autora: María Ejemplo", "María Ejemplo")]
    public void ExplicitAuthorMarkersRetainConservativeNames(string line, string author)
    {
        AuthorBylineCandidate candidate = AuthorBylinePolicy.Extract([line])
            .Should().ContainSingle().Subject;

        candidate.Role.Should().Be(AuthorCreditRole.Author);
        candidate.Names.Should().Equal(author);
    }

    [Fact]
    public void ExplicitCoauthorsRemainOrdered()
    {
        AuthorBylineCandidate candidate = AuthorBylinePolicy.Extract(
            ["By First Writer and Second Writer"])
            .Should().ContainSingle().Subject;

        candidate.Names.Should().Equal("First Writer", "Second Writer");
    }

    [Theory]
    [InlineData("By the time we arrived home")]
    [InlineData("By chapter 2 the mystery deepens")]
    [InlineData("A mention of First Writer in prose")]
    [InlineData("By First Writer; Second Writer")]
    [InlineData("By First Writer (Editor)")]
    [InlineData("By First Writer with Second Writer")]
    [InlineData("By ---")]
    public void IncidentalOrAmbiguousTextDoesNotBecomeAByline(string line)
    {
        AuthorBylinePolicy.Extract([line]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Edited by Editorial Person", AuthorCreditRole.Editor)]
    [InlineData("Editor: Editorial Person", AuthorCreditRole.Editor)]
    [InlineData("Translated by Translation Person", AuthorCreditRole.Translator)]
    [InlineData("Translator: Translation Person", AuthorCreditRole.Translator)]
    [InlineData("Illustrated by Visual Artist", AuthorCreditRole.Illustrator)]
    [InlineData("Foreword by Other Writer", AuthorCreditRole.ForewordWriter)]
    [InlineData("Published by Publisher Company", AuthorCreditRole.Publisher)]
    [InlineData("Dedicated to Other Person", AuthorCreditRole.Dedication)]
    [InlineData("Traduit par Autre Personne", AuthorCreditRole.Translator)]
    public void NonAuthorRolesRemainExplicit(string line, AuthorCreditRole role)
    {
        AuthorBylineCandidate candidate = AuthorBylinePolicy.Extract([line])
            .Should().ContainSingle().Subject;

        candidate.Role.Should().Be(role);
    }

    [Fact]
    public void InputAndLineLengthsAreBounded()
    {
        string[] lines = Enumerable.Repeat("noise", AuthorBylinePolicy.MaximumLines)
            .Append("By Too Late")
            .Prepend("By " + new string('A', AuthorBylinePolicy.MaximumLineCharacters))
            .ToArray();

        AuthorBylinePolicy.Extract(lines).Should().BeEmpty();
    }
}
