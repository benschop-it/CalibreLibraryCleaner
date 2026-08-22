using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class EditionMetadataAuthorWritePolicyTests
{
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
