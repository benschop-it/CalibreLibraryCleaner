using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class AuthorSourcePreservationPolicyTests
{
    [Fact]
    public void MalformedPseudonymAnnotationIsFlaggedWithoutGuessingAReplacement()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve("The Book",
            [new(new(1), "Morgan Vale (pseud. van Alex Writer", "Vale, Morgan")], false, []);

        resolution.Status.Should().Be(AuthorResolutionStatus.Suspect);
        resolution.ReasonCodes.Should().Contain(AuthorSuspicionReasonCodes.PseudonymAnnotation);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
        resolution.ProposedDisplayNames.Should().BeEmpty();
    }

    [Fact]
    public void PseudonymAnnotationParserRejectsOversizedAndMalformedUtf16Values()
    {
        string oversized = new string('a', 513) + " (pseud.)";
        AuthorPseudonymAnnotationPolicy.TryRemove(oversized, out _).Should().BeFalse();
        AuthorPseudonymAnnotationPolicy.TryRemove("\ud800 (pseud.)", out _).Should().BeFalse();
    }

    [Fact]
    public void PseudonymReviewPreservesOtherAuthorsAndRejectsCollapsedDuplicateAuthors()
    {
        BookAuthor[] authors =
        [
            new(new(1), "Morgan Vale (pseud. van Alex Writer)", "Vale, Morgan"),
            new(new(2), "Reliable Writer", "Writer, Reliable"),
        ];
        AuthorPseudonymAnnotationPolicy.TryNormalizeBook(authors, out IReadOnlyList<string> names).Should().BeTrue();
        names.Should().Equal("Morgan Vale", "Reliable Writer");
        AuthorPseudonymAnnotationPolicy.TryNormalizeBook(
            [authors[0], new(new(3), "Morgan Vale", "Vale, Morgan")], out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PseudonymRemovalIsAnUncheckedProposalEvenForProtectedAuthors(bool verified)
    {
        BookAuthor author = new(new(1), "Vale, Morgan pseud. van Alex Writer", "Vale, Morgan");

        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve("The Book", [author], verified, []);

        resolution.ProposalSource.Should().Be(AuthorProposalSource.PseudonymAnnotationRemoval);
        resolution.ProposedDisplayNames.Should().Equal("Morgan Vale");
        resolution.ProposedSortNames.Should().BeEmpty();
        resolution.SelectedByDefault.Should().BeFalse();
        resolution.ReasonCodes.Should().Contain(AuthorSuspicionReasonCodes.PseudonymAnnotation);
        author.Name.Should().Be("Vale, Morgan pseud. van Alex Writer");
        author.SortName.Should().Be("Vale, Morgan");
    }

    [Theory]
    [InlineData("Morgan Vale (pseud. van Alex Writer)", "Morgan Vale")]
    [InlineData("Morgan Vale (PSEUDONIEM van Alex Writer)", "Morgan Vale")]
    [InlineData("Morgan Vale (is Pseud.)", "Morgan Vale")]
    [InlineData("[pseud.] Morgan Vale", "Morgan Vale")]
    [InlineData("Morgan [pseud. ] Vale", "Morgan Vale")]
    [InlineData("Morgan (Pseud. Alex Writer) Vale", "Morgan Vale")]
    [InlineData("Vale, Morgan pseud. van Alex Writer", "Vale, Morgan")]
    [InlineData("Vale, Morgan is pseud", "Vale, Morgan")]
    [InlineData("Morgan Vale pseudoniem van Alex Writer", "Morgan Vale")]
    public void PseudonymAnnotationRemovalPreservesPublishedName(string value, string expected)
    {
        AuthorPseudonymAnnotationPolicy.TryRemove(value, out string? cleaned).Should().BeTrue();
        cleaned.Should().Be(expected);
        AuthorPseudonymAnnotationPolicy.ForMatching(cleaned!).Should().Be(expected);
        AuthorPseudonymAnnotationPolicy.TryRemove(cleaned, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Morgan Vale")]
    [InlineData("Morgan Vale (editor)")]
    [InlineData("Morgan Vale (born 1970)")]
    [InlineData("Morgan Vale (pseud. van Alex Writer")]
    [InlineData("Morgan Vale [pseud. van Alex Writer)")]
    [InlineData("Morgan Vale (pseud. van Alex (Other) Writer)")]
    [InlineData("Pseudonymous Stories")]
    [InlineData("Morgan pseudoscience Vale")]
    [InlineData("[pseud.]")]
    [InlineData("(pseud. van Alex Writer)")]
    public void PseudonymAnnotationRemovalLeavesUnmarkedOrAmbiguousValuesUnchanged(string value)
    {
        AuthorPseudonymAnnotationPolicy.TryRemove(value, out _).Should().BeFalse();
        AuthorPseudonymAnnotationPolicy.ForMatching(value).Should().Be(value);
    }

    [Fact]
    public void CredibleCurrentAuthorWinsOverEmbeddedUnknownAndDisagreement()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [Author("Reliable Writer")],
            verifiedAppliedOnlineAuthors: false,
            [Evidence(AuthorEvidenceSource.EpubPackageMetadata, "Unknown"),
             Evidence(AuthorEvidenceSource.PdfDocumentMetadata, "Different Writer")]);

        resolution.Status.Should().Be(AuthorResolutionStatus.Known);
        resolution.CurrentProtection.Should().Be(CurrentAuthorProtection.CredibleCurrentMetadata);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
    }

    [Fact]
    public void VerifiedAppliedAuthorIsProtectedEvenWhenItsShapeIsSuspicious()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [Author("1922 Reliable Writer")],
            verifiedAppliedOnlineAuthors: true,
            [Evidence(AuthorEvidenceSource.EpubPackageMetadata, "Different Writer")]);

        resolution.Status.Should().Be(AuthorResolutionStatus.Known);
        resolution.CurrentProtection.Should().Be(CurrentAuthorProtection.VerifiedAppliedOnlineMetadata);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
    }

    [Fact]
    public void EmbeddedUnknownNeverBecomesAProposal()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [Author("Unknown")],
            verifiedAppliedOnlineAuthors: false,
            [Evidence(AuthorEvidenceSource.EpubPackageMetadata, "Unknown")]);

        resolution.Status.Should().Be(AuthorResolutionStatus.Unresolved);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
        resolution.ProposedDisplayNames.Should().BeEmpty();
    }

    [Fact]
    public void CompatibleCachedEvidenceProducesAnUncheckedProposal()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [Author("Unknown")],
            verifiedAppliedOnlineAuthors: false,
            [Evidence(AuthorEvidenceSource.EpubPackageMetadata, "Reliable Writer"),
             Evidence(AuthorEvidenceSource.PdfDocumentMetadata, "Reliable Writer")]);

        resolution.Status.Should().Be(AuthorResolutionStatus.Suspect);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.CachedEmbeddedMetadata);
        resolution.ProposedDisplayNames.Should().Equal("Reliable Writer");
        resolution.SelectedByDefault.Should().BeFalse();
    }

    [Fact]
    public void PunctuationOnlyEmbeddedVariantDoesNotConfirmSuspectCurrentMetadata()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "Chelsea Handler",
            [Author("Are You There| Vodka| It's Me Chelsea (html)")],
            verifiedAppliedOnlineAuthors: false,
            [Evidence(
                AuthorEvidenceSource.EpubPackageMetadata,
                "Are You There, Vodka, It's Me Chelsea (html)")]);

        resolution.Status.Should().Be(AuthorResolutionStatus.Suspect);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
        resolution.ProposedDisplayNames.Should().BeEmpty();
    }

    [Fact]
    public void StrictCommaFormEmbeddedEvidenceProducesDisplayAndSortProposal()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [Author("Unknown")],
            verifiedAppliedOnlineAuthors: false,
            [Evidence(AuthorEvidenceSource.EpubPackageMetadata, "Writer, Reliable")]);

        resolution.ProposalSource.Should().Be(AuthorProposalSource.CachedEmbeddedMetadata);
        resolution.ProposedDisplayNames.Should().Equal("Reliable Writer");
        resolution.ProposedSortNames.Should().Equal("Writer, Reliable");
        resolution.SelectedByDefault.Should().BeFalse();
    }

    [Fact]
    public void DisagreeingCachedEvidenceRemainsConflicting()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [Author("Unknown")],
            verifiedAppliedOnlineAuthors: false,
            [Evidence(AuthorEvidenceSource.EpubPackageMetadata, "First Writer"),
             Evidence(AuthorEvidenceSource.PdfDocumentMetadata, "Second Writer")]);

        resolution.Status.Should().Be(AuthorResolutionStatus.Conflicting);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
    }

    [Fact]
    public void ExistingConservativeNormalizationIsTheOnlyDefaultSelection()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [new(new(1), "Writer| Reliable", "Writer, Reliable")],
            verifiedAppliedOnlineAuthors: false,
            []);

        resolution.ProposalSource.Should().Be(AuthorProposalSource.ConservativeNameOrder);
        resolution.ProposedDisplayNames.Should().Equal("Reliable Writer");
        resolution.ProposedSortNames.Should().Equal("Writer, Reliable");
        resolution.SelectedByDefault.Should().BeTrue();
        resolution.ReasonCodes.Should().Contain(AuthorSuspicionReasonCodes.ConservativeNameOrder);
    }

    [Fact]
    public void DeterministicTypographyProducesCheckedProposalForVerifiedAuthor()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [new(new(1), "J.K. Rowling", "Rowling, J.K.")],
            verifiedAppliedOnlineAuthors: true,
            []);

        resolution.CurrentProtection.Should().Be(CurrentAuthorProtection.VerifiedAppliedOnlineMetadata);
        resolution.ProposalSource.Should().Be(AuthorProposalSource.DeterministicTypography);
        resolution.ProposedDisplayNames.Should().Equal("J. K. Rowling");
        resolution.ProposedSortNames.Should().Equal("Rowling, J. K.");
        resolution.SelectedByDefault.Should().BeTrue();
        resolution.ReasonCodes.Should().Contain(AuthorSuspicionReasonCodes.DisplayTypography);
    }

    [Fact]
    public void FrontMatterAuthorProducesAnUncheckedContentDerivedProposal()
    {
        AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
            "The Book",
            [Author("Unknown")],
            verifiedAppliedOnlineAuthors: false,
            [Evidence(AuthorEvidenceSource.PdfFrontMatter, "Reliable Writer")]);

        resolution.ProposalSource.Should().Be(AuthorProposalSource.ContentDerivedFrontMatter);
        resolution.ProposedDisplayNames.Should().Equal("Reliable Writer");
        resolution.SelectedByDefault.Should().BeFalse();
    }

    private static BookAuthor Author(string value) => new(new(1), value, value);

    private static AuthorEvidenceObservation Evidence(AuthorEvidenceSource source, string author) =>
        new(source, $"{source}/1", [author]);
}
