using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class BibliographicEvidenceTests
{
    private static readonly BibliographicProviderIdentity Provider = new(
        "open-library", "open-library-search/1.0.0");
    private static readonly DateTimeOffset RetrievedAt = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QueryIdentityIsDeterministicAndDoesNotExposeMetadata()
    {
        BibliographicLookupQuery first = Query(1, "Alice in Wonderland Illustrated", "L. Carroll");
        BibliographicLookupQuery second = Query(2, "Alice in Wonderland Illustrated", "L. Carroll");

        first.QueryIdentity.Should().Be(second.QueryIdentity);
        first.QueryIdentity.Should().HaveLength(64).And.NotContain("ALICE");
        first.Fields.Should().HaveFlag(BibliographicQueryFields.Title);
        first.Fields.Should().HaveFlag(BibliographicQueryFields.Authors);
        first.Fields.Should().HaveFlag(BibliographicQueryFields.Language);
    }

    [Fact]
    public void UniqueCompatibleWorkResolvesDespiteEditionMarkerVariant()
    {
        BibliographicLookupQuery query = Query(1, "Alice in Wonderland Illustrated", "L. Carroll");
        BibliographicSearchResult search = new(
            Provider,
            BibliographicSearchStatus.Success,
            RetrievedAt,
            [new("/works/OL138052W", "Alice's Adventures in Wonderland", ["Lewis Carroll"], ["eng"], 480)]);

        BibliographicWorkResolution resolution = BibliographicResolutionPolicy.Resolve(query, search);

        resolution.Status.Should().Be(BibliographicResolutionStatus.Matched);
        resolution.WorkId.Should().Be("/works/OL138052W");
        resolution.QueryFields.Should().Be(query.Fields);
    }

    [Fact]
    public void DuplicateCompatibleWorksRemainAmbiguousWithoutDominance()
    {
        BibliographicLookupQuery query = Query(1, "Pride and Prejudice", "Jane Austen");
        BibliographicSearchResult search = new(
            Provider,
            BibliographicSearchStatus.Success,
            RetrievedAt,
            [
                new("/works/OL66554W", "Pride and Prejudice", ["Jane Austen"], ["eng"], 10),
                new("/works/OL15165350W", "Pride and Prejudice", ["Jane Austen"], ["eng"], 4),
            ]);

        BibliographicWorkResolution resolution = BibliographicResolutionPolicy.Resolve(query, search);

        resolution.Status.Should().Be(BibliographicResolutionStatus.Ambiguous);
        resolution.WorkId.Should().BeNull();
    }

    [Fact]
    public void ClearlyDominantExactWorkResolvesDeterministically()
    {
        BibliographicLookupQuery query = Query(1, "Pride and Prejudice", "Jane Austen");
        BibliographicSearchResult search = new(
            Provider,
            BibliographicSearchStatus.Success,
            RetrievedAt,
            [
                new("/works/OL15165350W", "Pride and Prejudice", ["Jane Austen"], ["eng"], 4),
                new("/works/OL66554W", "Pride and Prejudice", ["Jane Austen"], ["eng"], 200),
            ]);

        BibliographicWorkResolution resolution = BibliographicResolutionPolicy.Resolve(query, search);

        resolution.Status.Should().Be(BibliographicResolutionStatus.Matched);
        resolution.WorkId.Should().Be("/works/OL66554W");
    }

    [Fact]
    public void SameResolvedWorkAddsAnchorWithBoundedProvenance()
    {
        BookCandidatePair pair = Pair(1, 2);
        Dictionary<CalibreBookId, BibliographicWorkResolution> resolutions = new()
        {
            [new(1)] = Resolution(1, "/works/OL138052W"),
            [new(2)] = Resolution(2, "/works/OL138052W"),
        };

        BookCandidatePair enriched = BibliographicPairEvidencePolicy.Enrich([pair], resolutions).Single();

        CandidateEvidence evidence = enriched.Evidence.Single(value =>
            value.Code == BibliographicPairEvidencePolicy.EvidenceCode);
        evidence.Strength.Should().Be(CandidateEvidenceStrength.Anchor);
        evidence.Provenance.Should().Be(new CandidateEvidenceProvenance(
            "open-library", "open-library-search/1.0.0", "/works/OL138052W"));
        BookCandidateDecisionPolicy.Decide([enriched]).Single().Disposition
            .Should().Be(CandidatePairDisposition.Anchor);
    }

    [Fact]
    public void BibliographicAnchorCannotOverrideDecisiveLanguageContradiction()
    {
        BookCandidatePair pair = new(
            new(new(1), new(2)),
            900,
            [new("MATCH.TITLE.TOKEN_MEDIUM", CandidateEvidenceStrength.Supporting)],
            [new("MATCH.LANGUAGE.CONFLICT")],
            needsContentEvidence: false);
        Dictionary<CalibreBookId, BibliographicWorkResolution> resolutions = new()
        {
            [new(1)] = Resolution(1, "/works/OL138052W"),
            [new(2)] = Resolution(2, "/works/OL138052W"),
        };

        BookCandidatePair enriched = BibliographicPairEvidencePolicy.Enrich([pair], resolutions).Single();

        BookCandidateDecisionPolicy.Decide([enriched]).Single().Disposition
            .Should().Be(CandidatePairDisposition.Rejected);
    }

    private static BibliographicLookupQuery Query(long id, string title, string author) => new(
        new(id),
        Provider,
        BibliographicQueryFields.Title | BibliographicQueryFields.Authors | BibliographicQueryFields.Language,
        null,
        title,
        [author],
        "en");

    private static BookCandidatePair Pair(long first, long second) => new(
        new(new(first), new(second)),
        900,
        [new("MATCH.TITLE.TOKEN_MEDIUM", CandidateEvidenceStrength.Supporting)],
        [],
        needsContentEvidence: true);

    private static BibliographicWorkResolution Resolution(long id, string workId) => new(
        new(id),
        Provider,
        new string('a', 64),
        BibliographicQueryFields.Title | BibliographicQueryFields.Authors,
        RetrievedAt,
        BibliographicResolutionStatus.Matched,
        workId);
}
