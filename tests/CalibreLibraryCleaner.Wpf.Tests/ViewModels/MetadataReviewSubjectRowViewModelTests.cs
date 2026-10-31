using System.Diagnostics;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class MetadataReviewSubjectRowViewModelTests
{
    [Fact]
    public void RevalidatedCachedProvenanceKeepsTheOriginalProviderAndInterpretationVersionsVisible()
    {
        EditionMetadataCacheOrigin origin = new(new("open-library", "legacy-provider/1"),
            "edition-metadata-query/1.6.0", "edition-metadata-proposal/1.0.0", new string('a', 64));
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, cacheOrigin: origin);

        row.Provenance.Should().Contain("cached source: legacy-provider/1")
            .And.Contain("edition-metadata-query/1.6.0").And.Contain("edition-metadata-proposal/1.0.0");
    }

    [Fact]
    public void LegacyCreditsUseIndependentlyCorrespondingCurrentOrderWithoutClaimingSourceOrder()
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, authors: ["Kemp, Kristen", "Lannert, Stacy"],
            currentAuthor: "Lannert| Stacy| Kemp| Kristen", authorOrderKnown: false);

        row.ProposedAuthors.Should().Be("Stacy Lannert, Kristen Kemp");
        row.FinalAuthors.Should().Be(row.ProposedAuthors);
        row.ProviderAuthors.Should().Be("Kemp, Kristen, Lannert, Stacy");
        row.Reasons.Should().Contain(EditionMetadataAuthorWritePolicy.LegacyAuthorOrderUnavailable);
        row.AuthorWriteNotice.Should().Contain("current credit order");
    }

    [Fact]
    public void LegacyCoauthorOrderWithoutIndependentCurrentCreditsIsWithheldButOtherFieldsRemainUsable()
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, authors: ["Kemp, Kristen", "Lannert, Stacy"],
            authorOrderKnown: false);

        row.ProposedAuthors.Should().Be("Unchanged (provider author field withheld)");
        row.FinalAuthors.Should().Be("Current Author");
        row.FinalPublisher.Should().Be("Proposed Publisher");
        row.AuthorWriteNotice.Should().Contain("order is unavailable").And.Contain("current author list will be retained");
    }

    [Theory]
    [InlineData("A.C. Baantjer")]
    [InlineData("Baantjer, A.C.")]
    public void ProposedAndFinalAuthorsUseDotAndSpaceInitialsInsteadOfRawProviderTypography(string providerAuthor)
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, authors: [providerAuthor], currentAuthor: "A. C. Baantjer");

        row.ProposedAuthors.Should().Be("A. C. Baantjer");
        row.FinalAuthors.Should().Be("A. C. Baantjer");
        row.ProviderAuthors.Should().Be(providerAuthor);
        row.Apply = false;
        row.FinalAuthors.Should().Be("A. C. Baantjer");
    }

    [Theory]
    [InlineData("Henry| Patrick", "Henry, Patrick", "Patrick Henry")]
    [InlineData("Henry| Patrick", "Henry| Patrick", "Patrick Henry")]
    [InlineData("Lannert| Stacy| Kemp| Kristen", "Lannert| Stacy| Kemp| Kristen", "Stacy Lannert, Kristen Kemp")]
    [InlineData("Lannert| Stacy| Kemp| Kristen", "Lannert, Stacy & Kemp, Kristen", "Stacy Lannert, Kristen Kemp")]
    public void ExplicitAuthorPairsShowGivenFirstCreditsInTheProposalAndCheckedPreview(
        string current, string provider, string expected)
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, authors: [provider], currentAuthor: current);

        row.CurrentAuthors.Should().Be(current);
        row.ProviderAuthors.Should().Be(provider);
        row.ProposedAuthors.Should().Be(expected);
        row.FinalAuthors.Should().Be(expected);
        row.Apply = false;
        row.FinalAuthors.Should().Be(current);
        row.Apply = true;
        row.FinalAuthors.Should().Be(expected);
    }

    [Theory]
    [InlineData("Victor [pseud. ] Victor [pseud.] Appleton")]
    [InlineData("Christer, Sam psued. van Michael Morley")]
    public void AnnotatedProviderEvidenceIsNotDisplayedOrAppliedAsAnAuthorReplacement(string providerAuthor)
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, authors: [providerAuthor]);

        row.ProposedAuthors.Should().Be("Unchanged (provider author field withheld)");
        row.ProviderAuthors.Should().Be(providerAuthor);
        row.FinalAuthors.Should().Be("Current Author");
        row.AuthorWriteNotice.Should().Contain("pseudonym annotations").And.Contain("current author list");
        row.Reasons.Should().Contain(EditionMetadataAuthorWritePolicy.PseudonymAnnotationWithheld);
        row.FinalPublisher.Should().Be("Proposed Publisher");
        row.Apply = false;
        row.FinalAuthors.Should().Be("Current Author");
        row.Apply = true;
        row.FinalAuthors.Should().Be("Current Author");
        row.FinalPublisher.Should().Be("Proposed Publisher");
    }

    [Fact]
    public void PreparedCombinedCreditsMatchTheFinalWritablePreview()
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, authors: ["Family, First & Other, Second"]);

        row.ProposedAuthors.Should().Be("First Family, Second Other");
        row.FinalAuthors.Should().Be(row.ProposedAuthors);
        row.ProviderAuthors.Should().Be("Family, First & Other, Second");
    }

    [Fact]
    public void ShowsCompactCurrentAndProposedMetadataIncludingCoverAvailability()
    {
        MetadataReviewSubjectRowViewModel row = Row(1, FusedEditionMetadataConfidence.Medium);

        row.CurrentTitle.Should().Be("Current Title");
        row.CurrentCover.Should().Be("Available");
        row.ProposedTitle.Should().Be("Proposed Title");
        row.ProposedAuthors.Should().Be("Proposed Author");
        row.ProposedIdentifiers.Should().Contain("ISBN:9780306406157");
        row.ProposedPublisher.Should().Be("Proposed Publisher");
        row.ProposedPublicationDate.Should().Be("2005-04-03");
        row.ProposedLanguages.Should().Be("en");
        row.ProposedSeries.Should().Be("Series #2");
        row.ProposedCover.Should().Be("Available (cover-1)");
        row.FinalTitle.Should().Be("Proposed Title");
        row.FinalIdentifiers.Should().Contain("google:preserved").And.Contain("isbn:9780306406157");
        row.FinalCover.Should().Be("Available");
        row.Provenance.Should().Contain("open-library").And.Contain("edition-1");
        row.Apply.Should().BeTrue();

        row.Apply = false;
        row.FinalTitle.Should().Be("Current Title");
        row.FinalAuthors.Should().Be("Current Author");
        row.FinalPublisher.Should().Be("Current Publisher");
        row.FinalCover.Should().Be("Available");
    }

    [Fact]
    public void RiskyMediumProposalIsUncheckedAndFilterableAsNeedsReview()
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Medium, requiresReview: true);

        row.Apply.Should().BeFalse();
        row.NeedsReview.Should().BeTrue();
        row.MatchesFilter(MetadataReviewFilterMode.NeedsReview).Should().BeTrue();
        row.MatchesFilter(MetadataReviewFilterMode.AppliedAutomatically).Should().BeFalse();
        row.FinalTitle.Should().Be("Current Title");

        row.Apply = true;
        row.NeedsReview.Should().BeTrue();
        row.MatchesFilter(MetadataReviewFilterMode.NeedsReview).Should().BeTrue();
        row.MatchesFilter(MetadataReviewFilterMode.AppliedAutomatically).Should().BeFalse();
        row.FinalTitle.Should().Be("Proposed Title");
    }

    [Fact]
    public void UnavailableRowShowsConcreteProviderFailure()
    {
        MetadataReviewSubjectRowViewModel row = Row(1, FusedEditionMetadataConfidence.Unavailable);

        row.ProviderOutcomes.Should().Be(
            "google-books: temporarily unavailable (HTTP 429 / quota or rate limit; GOOGLE_BOOKS.HTTP_429)");
        row.Provenance.Should().Be(row.ProviderOutcomes);
    }

    [Fact]
    public void AlreadyAppliedRowIsUncheckedDisabledAndSeparatelyFilterable()
    {
        MetadataReviewSubjectRowViewModel row = Row(
            1, FusedEditionMetadataConfidence.Low, alreadyApplied: true);

        row.Confidence.Should().Be("Already applied");
        row.Apply.Should().BeFalse();
        row.CanApply.Should().BeFalse();
        row.NeedsReview.Should().BeFalse();
        row.MatchesFilter(MetadataReviewFilterMode.AlreadyApplied).Should().BeTrue();
    }

    [Fact]
    public void TwentyThousandRowsMaterializeAndFilterDeterministically()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        MetadataReviewSubjectRowViewModel[] rows = Enumerable.Range(1, 20_000)
            .Select(index => Row(index, (index % 4) switch
            {
                0 => FusedEditionMetadataConfidence.High,
                1 => FusedEditionMetadataConfidence.Medium,
                2 => FusedEditionMetadataConfidence.Low,
                _ => FusedEditionMetadataConfidence.Unavailable,
            })).ToArray();
        MetadataReviewSubjectRowViewModel[] needsReview = rows.Where(value =>
            value.MatchesFilter(MetadataReviewFilterMode.NeedsReview)).ToArray();
        MetadataReviewSubjectRowViewModel[] automatic = rows.Where(value =>
            value.MatchesFilter(MetadataReviewFilterMode.AppliedAutomatically)).ToArray();
        MetadataReviewSubjectRowViewModel[] unavailable = rows.Where(value =>
            value.MatchesFilter(MetadataReviewFilterMode.Unavailable)).ToArray();
        stopwatch.Stop();

        rows.Should().HaveCount(20_000);
        needsReview.Should().HaveCount(5_000);
        automatic.Should().HaveCount(10_000);
        unavailable.Should().HaveCount(5_000);
        rows.Should().OnlyContain(value => value.MatchesFilter(MetadataReviewFilterMode.All));
        rows.Select(value => value.SubjectId.Value).Should().OnlyHaveUniqueItems();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    private static MetadataReviewSubjectRowViewModel Row(
        int id,
        FusedEditionMetadataConfidence confidence,
        bool alreadyApplied = false,
        bool requiresReview = false,
        IEnumerable<string>? authors = null,
        string? currentAuthor = null,
        bool authorOrderKnown = true,
        EditionMetadataCacheOrigin? cacheOrigin = null)
    {
        EditionMetadataProviderIdentity provider = new("open-library", "provider/1.0.0");
        EditionMetadataCandidate candidate = new(
            "work-1",
            "edition-1",
            "Proposed Title",
            authors ?? ["Proposed Author"],
            [new("isbn", "9780306406157")],
            "Proposed Publisher",
            new(2005, 4, 3),
            ["en"],
            "Series",
            2,
            new("cover-1", "https://example.test/cover.jpg"),
            authorOrderKnown);
        EditionMetadataProposal providerProposal = new(
            new(id),
            provider,
            EditionMetadataQueryFields.Identifier,
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            EditionMetadataProposalStatus.Proposed,
            candidate,
            10_008,
            ["METADATA.EDITION.ISBN_EXACT"],
            cacheOrigin: cacheOrigin);
        EditionMetadataProposal unavailableProposal = new(
            new(id),
            new("google-books", "provider/1.0.0"),
            EditionMetadataQueryFields.Identifier,
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            EditionMetadataProposalStatus.Unavailable,
            problemCode: "GOOGLE_BOOKS.HTTP_429");
        FusedEditionMetadataProposal proposal = confidence == FusedEditionMetadataConfidence.Unavailable
            ? new(
                confidence,
                null,
                null,
                [unavailableProposal],
                [],
                ["METADATA.FUSION.CONFIDENCE_UNAVAILABLE"])
            : new(
                confidence,
                provider,
                candidate,
                [providerProposal],
                [new(EditionMetadataField.Title, provider, candidate.EditionId)],
                ["METADATA.FUSION.TEST"]);
        MetadataReviewSubject subject = new(
            new($"metadata-review/book/{id}"),
            null,
            [new CalibreBookId(id)],
            new(id),
            proposal);
        ReviewedMetadataSubject reviewed = new(
            subject,
            alreadyApplied || requiresReview ? false : proposal.IsSelectedByDefault,
            false,
            null,
            alreadyApplied,
            requiresReview);
        CalibreBook current = new(
            new(id),
            "Current Title",
            "Author, Current",
            [new(new(id), currentAuthor ?? "Current Author", "Author, Current")],
            [new("isbn", "9780140328721"), new("google", "preserved")],
            [],
            $"Author/Book ({id})",
            new(
                "Current Publisher",
                new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero),
                "Current Series",
                1,
                ["en"],
                hasCover: true));
        return new(reviewed, current, (_, _) => Task.CompletedTask);
    }
}
