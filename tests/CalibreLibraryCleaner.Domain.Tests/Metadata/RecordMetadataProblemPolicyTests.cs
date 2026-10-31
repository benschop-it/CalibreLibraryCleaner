using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class RecordMetadataProblemPolicyTests
{
    [Theory]
    [InlineData("Jane Writer - The Mountain", "Jane Writer", RecordMetadataProblemPolicy.TitleContainsAuthor)]
    [InlineData("The Mountain / Jane Writer", "Jane Writer", RecordMetadataProblemPolicy.TitleContainsAuthor)]
    [InlineData("The Mountain | Jane Writer", "Jane Writer", RecordMetadataProblemPolicy.TitleContainsAuthor)]
    [InlineData("Jane Writer: The Mountain", "Jane Writer", RecordMetadataProblemPolicy.TitleContainsAuthor)]
    [InlineData("The Mountain", "The Mountain", AuthorSuspicionReasonCodes.MatchesTitle)]
    [InlineData("The Long Mountain Journey", "Long Mountain Journey", AuthorSuspicionReasonCodes.ResemblesTitle)]
    [InlineData("The Mountain", "Jane Writer [12]", RecordMetadataProblemPolicy.AuthorBracketedNumber)]
    [InlineData("The Mountain", "Jane Writer (2)", RecordMetadataProblemPolicy.AuthorBracketedNumber)]
    [InlineData("The Mountain", "Jane Writer [\u0661\u0662]", RecordMetadataProblemPolicy.AuthorBracketedNumber)]
    [InlineData("The Mountain", "Unknown", AuthorSuspicionReasonCodes.Placeholder)]
    [InlineData("The Mountain", "", AuthorSuspicionReasonCodes.Missing)]
    [InlineData("", "Jane Writer", "TITLE.MISSING")]
    [InlineData("The Mountain", "Jane || Writer", AuthorSuspicionReasonCodes.MalformedSeparator)]
    [InlineData("The Mountain", "Jane Writer (", "AUTHOR.UNBALANCED_DELIMITER")]
    public void ConcreteReviewSignalsAreExplainedWithoutChangingMatching(string title, string author, string reason)
    {
        RecordMetadataProblemPolicy.Assess(title, [author]).Should().Contain(reason);
    }

    [Theory]
    [InlineData("The Mountain", "Jane Writer")]
    [InlineData("The Life of Jane Writer", "Jane Writer")]
    [InlineData("Jane Writer and the Mountain", "Jane Writer")]
    [InlineData("A Book about Jane Writer: Her Life", "Jane Writer")]
    [InlineData("Writer - The Mountain", "Writer")]
    [InlineData("1984", "George Orwell")]
    [InlineData("Unknown", "Anonymous")]
    [InlineData("The Mountain", "Jane Writer (1920-1999)")]
    [InlineData("A Field Book", "\u674e\u767d")]
    [InlineData("The Mountain", "\u00c9. du Pr\u00e9")]
    public void PlausibleCatalogValuesAreNotProblemsMerelyBecauseVerificationIsMissing(string title, string author)
    {
        RecordMetadataProblemPolicy.Assess(title, [author]).Should().BeEmpty();
    }

    [Fact]
    public void CompleteAuthorCreditAndFieldBoundsAreRequired()
    {
        RecordMetadataProblemPolicy.Assess("Other Writer - The Mountain", ["Jane Writer"])
            .Should().NotContain(RecordMetadataProblemPolicy.TitleContainsAuthor);
        RecordMetadataProblemPolicy.Assess(new string('x', 513) + " - Jane Writer", ["Jane Writer"])
            .Should().Contain("TITLE.FIELD_LIMIT").And.NotContain(RecordMetadataProblemPolicy.TitleContainsAuthor);
    }
}
