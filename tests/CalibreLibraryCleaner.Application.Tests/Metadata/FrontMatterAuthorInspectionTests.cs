using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class FrontMatterAuthorInspectionTests
{
    [Fact]
    public void V1CeilingsCannotBeExpanded()
    {
        Action epub = () => _ = new FrontMatterInspectionLimits(4, 5, 32_768);
        Action pdf = () => _ = new FrontMatterInspectionLimits(3, 6, 32_768);
        Action text = () => _ = new FrontMatterInspectionLimits(3, 5, 32_769);

        epub.Should().Throw<ArgumentOutOfRangeException>();
        pdf.Should().Throw<ArgumentOutOfRangeException>();
        text.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ObservationRetainsCandidatesAndCoverageWithoutText()
    {
        FrontMatterAuthorObservation observation = new(
            "EPUB",
            new(10, new(new string('a', 64))),
            FrontMatterAuthorVersions.EpubExtractor,
            FrontMatterAuthorVersions.Limits,
            FrontMatterAuthorStatus.Available,
            [new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 2, "By")],
            requestedUnits: 3,
            completedUnits: 2,
            retainedUsefulCharacters: 1_024,
            truncated: false);

        observation.Candidates.Single().Names.Should().Equal("Reliable Writer");
        observation.Should().NotBeAssignableTo<string>();
    }
}
