using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class FrontMatterAuthorInspectionTests
{
    [Fact]
    public void TitleAndExplicitCreditRetainPairedLocations()
    {
        FrontMatterIdentityCandidate candidate = FrontMatterIdentityExtraction.Extract(
            ["Title: The Northern Valley", "By Reliable Writer"], 2).Should().ContainSingle().Subject;

        candidate.Title.Should().Be("The Northern Valley");
        candidate.Authors.Should().Equal("Reliable Writer");
        candidate.UnitNumber.Should().Be(2);
        candidate.TitleLineNumber.Should().Be(1);
        candidate.AuthorLineNumber.Should().Be(2);
        candidate.ExplicitCredit.Should().BeTrue();
    }

    [Fact]
    public void UnlabelledPairIsCandidateNotIndependentConfirmation()
    {
        FrontMatterIdentityCandidate candidate = FrontMatterIdentityExtraction.Extract(
            ["The Northern Valley", "Reliable Writer"], 1).Should().ContainSingle().Subject;

        candidate.ExplicitCredit.Should().BeFalse();
        FrontMatterIdentityExtraction.Extract(
            ["Copyright Reliable Writer", "Translated by Other Writer"], 1).Should().BeEmpty();
        FrontMatterIdentityExtraction.Extract([], 1).Should().BeEmpty();
    }

    [Fact]
    public void LabelledSubtitleDoesNotReplaceTheMainTitle()
    {
        FrontMatterIdentityCandidate candidate = FrontMatterIdentityExtraction.Extract(
            ["Title: A Northern Valley", "Subtitle: An Oral History", "By Reliable Writer"], 1)
            .Should().ContainSingle().Subject;

        candidate.Title.Should().Be("A Northern Valley");
        candidate.Subtitle.Should().Be("An Oral History");
        candidate.ExplicitCredit.Should().BeTrue();
    }

    [Fact]
    public void LongPairedSubtitlesKeepTheirEvidenceWithoutTruncatingTheMainBibliographicTitle()
    {
        string title = new('A', 500);
        string subtitle = new('B', 40);
        FrontMatterIdentityCandidate candidate = new(title, ["Reliable Writer"], 1, 1, 3, true, subtitle);

        candidate.CompleteTitle.Should().HaveLength(542);
        candidate.BibliographicTitle.Should().Be(title);
        candidate.Subtitle.Should().Be(subtitle);
    }

    [Fact]
    public void HeadingAloneDoesNotProveATitleAndAdvertisingDoesNotBecomeIdentity()
    {
        FrontMatterIdentityExtraction.Extract(
            ["A Heading", "By Reliable Writer"], 1, [1]).Should().ContainSingle()
            .Which.ExplicitCredit.Should().BeFalse();
        FrontMatterIdentityExtraction.Extract(
            ["Other books by Reliable Writer", "Title: Another Book", "By Reliable Writer"], 1)
            .Should().BeEmpty();
    }

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
