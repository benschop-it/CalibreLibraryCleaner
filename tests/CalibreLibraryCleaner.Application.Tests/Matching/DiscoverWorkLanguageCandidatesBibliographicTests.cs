using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Matching;

public sealed class DiscoverWorkLanguageCandidatesBibliographicTests
{
    [Fact]
    public async Task UnavailableLocalContentCanBecomeProviderBackedGroup()
    {
        BibliographicProviderIdentity providerIdentity = new(
            "open-library", "open-library-search-1.0.0");
        IBibliographicProvider provider = A.Fake<IBibliographicProvider>();
        A.CallTo(() => provider.Identity).Returns(providerIdentity);
        A.CallTo(() => provider.SearchAsync(A<BibliographicLookupQuery>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(new BibliographicSearchResult(
                providerIdentity,
                BibliographicSearchStatus.Success,
                new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
                [new BibliographicWorkCandidate(
                    "/works/OL138052W",
                    "Alice's Adventures in Wonderland",
                    ["Lewis Carroll"],
                    ["eng"],
                    480)])));
        IBibliographicResolutionCache cache = A.Fake<IBibliographicResolutionCache>();
        A.CallTo(() => cache.TryReadAsync(
                A<BibliographicLookupQuery>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(Task.FromResult<BibliographicWorkResolution?>(null));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        ResolveBibliographicEvidenceUseCase bibliography = new(provider, cache, clock, new());
        IEpubContentSignatureInspector inspector = A.Fake<IEpubContentSignatureInspector>();
        IEpubContentSignatureCache signatureCache = A.Fake<IEpubContentSignatureCache>();
        DiscoverWorkLanguageCandidatesUseCase discover = new(
            new(inspector, signatureCache), bibliography);
        CalibreBook[] books =
        [
            Book(1, "Alice's Adventures in Wonderland", "Lewis Carroll"),
            Book(2, "Alice in Wonderland Illustrated", "L. Carroll"),
        ];

        WorkLanguageDiscoveryResult result = await discover.ExecuteAsync(
            books, [], [], 1, null, CancellationToken.None);

        result.Groups.Should().ContainSingle();
        WorkLanguageCandidateGroup group = result.Groups.Single();
        group.Evidence.Should().Contain(value =>
            value.Code == BibliographicPairEvidencePolicy.EvidenceCode
            && value.Provenance == new CandidateEvidenceProvenance(
                "open-library", "open-library-search-1.0.0", "/works/OL138052W"));
        group.PolicyVersion.Should().Be(MatchingPolicyVersion.V5);
        result.Summary.BibliographicQueries.Should().Be(2);
        result.Summary.BibliographicProviderRequests.Should().Be(2);
        result.Summary.BibliographicMatchedRecords.Should().Be(2);
        result.Summary.BibliographicFailures.Should().Be(0);
    }

    private static CalibreBook Book(long id, string title, string author) => new(
        new(id),
        title,
        author,
        [new(null, author, author)],
        [],
        [],
        $"Book {id}",
        new(languages: ["eng"]));
}
