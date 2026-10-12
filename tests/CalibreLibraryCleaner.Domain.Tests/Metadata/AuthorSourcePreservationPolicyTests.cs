using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class AuthorSourcePreservationPolicyTests
{
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
