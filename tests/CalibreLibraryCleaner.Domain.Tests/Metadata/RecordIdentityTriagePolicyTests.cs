using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class RecordIdentityTriagePolicyTests
{
    [Theory]
    [InlineData("(1997 The Silent Valley", "Writer", "TITLE.UNBALANCED_DELIMITER")]
    [InlineData("1997 The Silent Valley", "Writer", "TITLE.YEAR_PREFIX")]
    [InlineData("12. The Silent Valley", "Writer", "TITLE.NUMBER_PREFIX")]
    [InlineData("The Silent Valley", "Writer vol. 12", "AUTHOR.VOLUME_MARKER")]
    [InlineData("The Silent Valley", "Writer (", "AUTHOR.UNBALANCED_DELIMITER")]
    [InlineData("The Silent Valley", "Writer || Editor", "AUTHOR.DAMAGED_SEPARATOR")]
    public void GeneralDamageFamiliesHaveFieldSensitiveReasons(string title, string author, string reason)
    {
        RecordIdentityTriagePolicy.Assess(title, [author]).ReasonCodes.Should().Contain(reason);
    }

    [Theory]
    [InlineData("Unknown", "李白")]
    [InlineData("User", "É. du Pré")]
    [InlineData("1984", "O'Neill")]
    [InlineData("Anonymous", "Anonymous")]
    [InlineData("A Life", "Writer (1920-1999)")]
    [InlineData("A Life", "Studio 54")]
    [InlineData("A Life", "Q. 10 Public")]
    [InlineData("A Life", "王小明")]
    [InlineData("A Life", "И. П. Автор")]
    [InlineData("A Life", "Research Group 12")]
    public void LegitimateCounterexamplesAreNotMaterialIdentityFailures(string title, string author)
    {
        RecordIdentityTriagePolicy.Assess(title, [author]).MaterialIdentityProblem.Should().BeFalse();
    }

    [Fact]
    public void InputBeyondBoundsIsIncompleteRatherThanInventedDamage()
    {
        RecordIdentityTriageAssessment result = RecordIdentityTriagePolicy.Assess(
            new string('x', 513), ["Writer"]);

        result.ReasonCodes.Should().Contain("TITLE.FIELD_LIMIT");
        result.MaterialIdentityProblem.Should().BeFalse();
    }
}
