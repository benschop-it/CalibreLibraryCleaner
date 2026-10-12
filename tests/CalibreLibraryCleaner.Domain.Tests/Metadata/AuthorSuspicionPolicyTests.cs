using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class AuthorSuspicionPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("AUTHOR UNKNOWN")]
    [InlineData("N/A")]
    public void MissingAndPlaceholderAuthorsRemainUnresolved(string? author)
    {
        AuthorSuspicionAssessment assessment = AuthorSuspicionPolicy.Assess(
            "A Credible Title", author is null ? [] : [author]);

        assessment.Status.Should().Be(AuthorResolutionStatus.Unresolved);
        assessment.ReasonCodes.Should().Contain(author is null or ""
            ? AuthorSuspicionReasonCodes.Missing
            : AuthorSuspicionReasonCodes.Placeholder);
    }

    [Theory]
    [InlineData("1922 Jane Writer", "AUTHOR.NUMBER_PREFIXED")]
    [InlineData("Jane Writer (1922)", "AUTHOR.PARENTHESIZED_NUMBER")]
    [InlineData("Family| Given| Other", "AUTHOR.MALFORMED_SEPARATOR")]
    [InlineData("Family| Given, Other", "AUTHOR.MALFORMED_SEPARATOR")]
    [InlineData("Jane Writer; John Writer", "AUTHOR.MALFORMED_SEPARATOR")]
    public void DeterministicSignalsRequireReview(string author, string reason)
    {
        AuthorSuspicionAssessment assessment = AuthorSuspicionPolicy.Assess("A Credible Title", [author]);

        assessment.Status.Should().Be(AuthorResolutionStatus.Suspect);
        assessment.ReasonCodes.Should().Contain(reason);
    }

    [Fact]
    public void ExactAndStrongTitleResemblanceAreSeparateSignals()
    {
        AuthorSuspicionPolicy.Assess("The Long Road Home", ["The Long Road Home"])
            .ReasonCodes.Should().Equal(AuthorSuspicionReasonCodes.MatchesTitle);
        AuthorSuspicionPolicy.Assess("The Long Road Home", ["The Long Road Home Revised"])
            .ReasonCodes.Should().Equal(AuthorSuspicionReasonCodes.ResemblesTitle);
    }

    [Theory]
    [InlineData("Prince")]
    [InlineData("J. R. R. Tolkien")]
    [InlineData("Gabriel Garcia Marquez")]
    [InlineData("Institute of Electrical and Electronics Engineers")]
    [InlineData("Studio 54 Press")]
    [InlineData("Ludwig van Beethoven")]
    [InlineData("Q. 10 Public")]
    public void OrdinaryNamesAndInternalNumbersRemainKnown(string author)
    {
        AuthorSuspicionAssessment assessment = AuthorSuspicionPolicy.Assess("Unrelated Book Title", [author]);

        assessment.Status.Should().Be(AuthorResolutionStatus.Known);
        assessment.ReasonCodes.Should().BeEmpty();
    }

    [Fact]
    public void ReasonCodesAreUniqueAndOrdinallyStable()
    {
        AuthorSuspicionAssessment assessment = AuthorSuspicionPolicy.Assess(
            "1922 Long Road Home (1922)", ["1922 Long Road Home (1922)"]);

        assessment.ReasonCodes.Should().Equal(
            AuthorSuspicionReasonCodes.MatchesTitle,
            AuthorSuspicionReasonCodes.NumberPrefixed,
            AuthorSuspicionReasonCodes.ParenthesizedNumber);
    }
}
