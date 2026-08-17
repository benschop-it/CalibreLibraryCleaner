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
        row.Provenance.Should().Contain("open-library").And.Contain("edition-1");
        row.Apply.Should().BeTrue();
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
        FusedEditionMetadataConfidence confidence)
    {
        EditionMetadataProviderIdentity provider = new("open-library", "provider/1.0.0");
        EditionMetadataCandidate candidate = new(
            "work-1",
            "edition-1",
            "Proposed Title",
            ["Proposed Author"],
            [new("isbn", "9780306406157")],
            "Proposed Publisher",
            new(2005, 4, 3),
            ["en"],
            "Series",
            2,
            new("cover-1", "https://example.test/cover.jpg"));
        EditionMetadataProposal providerProposal = new(
            new(id),
            provider,
            EditionMetadataQueryFields.Identifier,
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            EditionMetadataProposalStatus.Proposed,
            candidate,
            10_008,
            ["METADATA.EDITION.ISBN_EXACT"]);
        FusedEditionMetadataProposal proposal = confidence == FusedEditionMetadataConfidence.Unavailable
            ? new(
                confidence,
                null,
                null,
                [],
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
            proposal.IsSelectedByDefault,
            false,
            null);
        CalibreBook current = new(
            new(id),
            "Current Title",
            "Author, Current",
            [new(new(id), "Current Author", "Author, Current")],
            [new("isbn", "9780140328721")],
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
