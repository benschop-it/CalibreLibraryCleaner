using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class MetadataReviewDecisionPolicyTests
{
    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly LibraryStateRevision Revision = new(7);

    [Fact]
    public void CompatibleOverrideRestoresAndKeeperRetargetPreservesDecisionKey()
    {
        MetadataReviewSubject original = Subject(FusedEditionMetadataConfidence.High, targetId: 1);
        MetadataReviewDecision decision = MetadataReviewDecisionPolicy.CreateOverride(
            original, Generation, Revision, apply: false)!;
        MetadataReviewSubject retargeted = original.Retarget(new(2));

        ReviewedMetadataSubject reviewed = MetadataReviewDecisionPolicy.Reconcile(
            [retargeted], Generation, Revision, [decision]).Single();

        reviewed.Apply.Should().BeFalse();
        reviewed.IsOverride.Should().BeTrue();
        reviewed.DecisionKey.Should().Be(decision.Key);
        reviewed.Subject.TargetBookId.Should().Be(new CalibreBookId(2));
        reviewed.Subject.Proposal.Should().BeSameAs(original.Proposal);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("policy")]
    [InlineData("edition")]
    [InlineData("provider-version")]
    public void IncompatibleDecisionResetsToConfidenceDefault(string mismatch)
    {
        MetadataReviewSubject subject = Subject(FusedEditionMetadataConfidence.High, targetId: 1);
        MetadataReviewDecisionKey current = MetadataReviewDecisionPolicy.CreateKey(
            subject, Generation, Revision);
        MetadataReviewDecisionKey stale = mismatch switch
        {
            "generation" => new(current.SubjectId, current.ProposalPolicyVersion, current.EditionIdentity,
                new(Guid.Parse("11111111-2222-3333-4444-555555555555")), current.Revision),
            "revision" => new(current.SubjectId, current.ProposalPolicyVersion, current.EditionIdentity,
                current.GenerationId, new(8)),
            "policy" => new(current.SubjectId, "edition-metadata-fusion/0.0.0", current.EditionIdentity,
                current.GenerationId, current.Revision),
            "edition" => new(current.SubjectId, current.ProposalPolicyVersion,
                new(current.EditionIdentity.Provider, "different-edition"), current.GenerationId, current.Revision),
            "provider-version" => new(current.SubjectId, current.ProposalPolicyVersion,
                new(new(current.EditionIdentity.Provider.Id, "provider/2.0.0"), current.EditionIdentity.EditionId),
                current.GenerationId, current.Revision),
            _ => throw new InvalidOperationException(),
        };

        ReviewedMetadataSubject reviewed = MetadataReviewDecisionPolicy.Reconcile(
            [subject], Generation, Revision, [new(stale, false)]).Single();

        reviewed.Apply.Should().BeTrue();
        reviewed.IsOverride.Should().BeFalse();
        reviewed.DecisionKey.Should().BeNull();
    }

    [Fact]
    public void LowAndUnavailableDefaultUncheckedAndUnavailableCannotCreateOverride()
    {
        MetadataReviewSubject low = Subject(FusedEditionMetadataConfidence.Low, targetId: 1);
        MetadataReviewSubject unavailable = UnavailableSubject();

        IReadOnlyList<ReviewedMetadataSubject> reviewed = MetadataReviewDecisionPolicy.Reconcile(
            [unavailable, low], Generation, Revision, []);

        reviewed.Should().OnlyContain(value => !value.Apply && !value.IsOverride);
        MetadataReviewDecisionPolicy.CreateOverride(unavailable, Generation, Revision, true)
            .Should().BeNull();
    }

    [Fact]
    public void ReturningToConfidenceDefaultRemovesOverride()
    {
        MetadataReviewSubject subject = Subject(FusedEditionMetadataConfidence.Medium, targetId: 1);

        MetadataReviewDecisionPolicy.CreateOverride(subject, Generation, Revision, apply: true)
            .Should().BeNull();
        MetadataReviewDecisionPolicy.CreateOverride(subject, Generation, Revision, apply: false)
            .Should().NotBeNull();
    }

    private static MetadataReviewSubject Subject(
        FusedEditionMetadataConfidence confidence,
        long targetId)
    {
        EditionMetadataProviderIdentity provider = new("open-library", "provider/1.0.0");
        EditionMetadataCandidate candidate = new(
            "work", "edition", "Title", ["Author"], [new("isbn", "9780306406157")]);
        EditionMetadataProposal providerProposal = new(
            new(1), provider, EditionMetadataQueryFields.Identifier, DateTimeOffset.UnixEpoch,
            EditionMetadataProposalStatus.Proposed, candidate, 10_000,
            ["METADATA.EDITION.ISBN_EXACT"]);
        FusedEditionMetadataProposal proposal = new(
            confidence, provider, candidate, [providerProposal],
            [new(EditionMetadataField.Title, provider, candidate.EditionId)],
            ["METADATA.FUSION.TEST"]);
        return new(
            new("metadata-review/group/group"),
            new("group"),
            [new CalibreBookId(1), new CalibreBookId(2)],
            new(targetId),
            proposal);
    }

    private static MetadataReviewSubject UnavailableSubject() => new(
        new("metadata-review/book/3"),
        null,
        [new CalibreBookId(3)],
        new(3),
        new(
            FusedEditionMetadataConfidence.Unavailable,
            null,
            null,
            [],
            [],
            ["METADATA.FUSION.CONFIDENCE_UNAVAILABLE"]));
}
