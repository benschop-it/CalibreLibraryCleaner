using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class WorkLanguageCandidateClustererTests
{
    [Fact]
    public void WeakAndUnavailableEdgesNeverCreateGroups()
    {
        BookCandidatePair pair = Pair(1, 2, 900, needsContent: true);

        IReadOnlyList<BookCandidateDecision> decisions = BookCandidateDecisionPolicy.Decide([pair]);
        IReadOnlyList<WorkLanguageCandidateGroup> groups = WorkLanguageCandidateClusterer.Cluster(
            [Profile(1), Profile(2)], decisions);

        decisions.Should().ContainSingle(value => value.Disposition == CandidatePairDisposition.Weak);
        groups.Should().BeEmpty();
    }

    [Fact]
    public void ExactAnchorCreatesReviewOnlyLanguageGroup()
    {
        BookCandidatePair pair = Pair(1, 2, 2_000, anchor: true, needsContent: false);

        WorkLanguageCandidateGroup group = WorkLanguageCandidateClusterer.Cluster(
            [Profile(1, "en"), Profile(2, "en")],
            BookCandidateDecisionPolicy.Decide([pair])).Single();

        group.Language.Should().Be("en");
        group.Confidence.Should().Be(WorkLanguageCandidateConfidence.Strong);
        group.CleanupEligibility.Should().Be(WorkLanguageCleanupEligibility.ReviewOnly);
        group.Members.Select(value => value.Value).Should().Equal(1, 2);
    }

    [Fact]
    public void KnownLanguageConflictRejectsSameLanguageGrouping()
    {
        BookCandidatePair pair = new(
            new(new(1), new(2)),
            2_000,
            [new("MATCH.BINARY.EXACT", CandidateEvidenceStrength.Anchor)],
            [new("MATCH.LANGUAGE.CONFLICT")],
            false);

        IReadOnlyList<BookCandidateDecision> decisions = BookCandidateDecisionPolicy.Decide([pair]);

        decisions.Should().ContainSingle(value => value.Disposition == CandidatePairDisposition.Rejected);
        WorkLanguageCandidateClusterer.Cluster([Profile(1, "en"), Profile(2, "nl")], decisions)
            .Should().BeEmpty();
    }

    [Fact]
    public void StrongChainCannotAttachThroughNonAnchorMember()
    {
        BookCandidatePair first = Pair(1, 2, 1_200, needsContent: false);
        BookCandidatePair chained = Pair(2, 3, 1_200, needsContent: false);

        WorkLanguageCandidateGroup group = WorkLanguageCandidateClusterer.Cluster(
            [Profile(1), Profile(2), Profile(3)],
            BookCandidateDecisionPolicy.Decide([first, chained])).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
    }

    [Fact]
    public void StrongRecordCanAttachDirectlyToStableAnchor()
    {
        BookCandidatePair seed = Pair(1, 2, 1_200, needsContent: false);
        BookCandidatePair attachment = Pair(1, 3, 1_200, needsContent: false);

        WorkLanguageCandidateGroup group = WorkLanguageCandidateClusterer.Cluster(
            [Profile(1), Profile(2), Profile(3)],
            BookCandidateDecisionPolicy.Decide([seed, attachment])).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2, 3);
        group.AnchorMembers.Select(value => value.Value).Should().Equal(1);
    }

    [Fact]
    public void CompleteComponentSeriesContradictionBlocksAnchorMerge()
    {
        BookCandidatePair first = Pair(1, 2, 2_000, anchor: true, needsContent: false);
        BookCandidatePair second = Pair(1, 3, 2_000, anchor: true, needsContent: false);

        WorkLanguageCandidateGroup group = WorkLanguageCandidateClusterer.Cluster(
            [Profile(1), Profile(2, series: "Series", seriesIndex: 1), Profile(3, series: "Series", seriesIndex: 2)],
            BookCandidateDecisionPolicy.Decide([first, second])).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
    }

    [Fact]
    public void EquivalentContentPromotesAmbiguousPairToAnchorDeterministically()
    {
        BookCandidatePair pair = Pair(1, 2, 800, needsContent: true);
        CandidateContentComparison comparison = new(
            ContentSimilarityClassification.EquivalentText, 10, 10, 10, 10, 4, 950);
        Dictionary<BookCandidatePairId, CandidateContentComparison> content = new() { [pair.Id] = comparison };

        IReadOnlyList<BookCandidateDecision> decisions = BookCandidateDecisionPolicy.Decide([pair], content);
        WorkLanguageCandidateGroup forward = WorkLanguageCandidateClusterer.Cluster(
            [Profile(1), Profile(2)], decisions).Single();
        WorkLanguageCandidateGroup reverse = WorkLanguageCandidateClusterer.Cluster(
            [Profile(2), Profile(1)], decisions.Reverse()).Single();

        decisions.Should().ContainSingle(value => value.Disposition == CandidatePairDisposition.Anchor);
        reverse.Should().BeEquivalentTo(forward);
    }

    [Fact]
    public void InitialsOnlyAliasCannotBridgeConflictingFullAuthors()
    {
        BookCandidatePair first = Pair(1, 2, 800, needsContent: true);
        BookCandidatePair second = Pair(2, 3, 800, needsContent: true);
        CandidateContentComparison equivalent = new(
            ContentSimilarityClassification.EquivalentText, 10, 10, 10, 10, 4, 950);
        Dictionary<BookCandidatePairId, CandidateContentComparison> content = new()
        {
            [first.Id] = equivalent,
            [second.Id] = equivalent,
        };

        WorkLanguageCandidateGroup group = WorkLanguageCandidateClusterer.Cluster(
            [
                Profile(1, author: "Joanne Kathleen Rowling"),
                Profile(2, author: "J. K. Rowling"),
                Profile(3, author: "John Kevin Rowling"),
            ],
            BookCandidateDecisionPolicy.Decide([first, second], content)).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
    }

    [Fact]
    public void ConfirmedWorksArePartitionedIntoSeparateKnownLanguageGroups()
    {
        BookCandidatePair english = Pair(1, 2, 800, needsContent: true);
        BookCandidatePair dutch = Pair(3, 4, 800, needsContent: true);
        CandidateContentComparison equivalent = new(
            ContentSimilarityClassification.EquivalentText, 10, 10, 10, 10, 4, 950);
        IReadOnlyList<WorkLanguageCandidateGroup> groups = WorkLanguageCandidateClusterer.Cluster(
            [Profile(1, "en"), Profile(2, "en"), Profile(3, "nl"), Profile(4, "nl")],
            BookCandidateDecisionPolicy.Decide(
                [english, dutch],
                new Dictionary<BookCandidatePairId, CandidateContentComparison>
                {
                    [english.Id] = equivalent,
                    [dutch.Id] = equivalent,
                }));

        groups.Should().HaveCount(2);
        groups.Select(value => value.Language).Should().Equal("en", "nl");
    }

    private static BookCandidatePair Pair(
        long first,
        long second,
        int score,
        bool anchor = false,
        bool needsContent = false) => new(
        new(new(first), new(second)),
        score,
        [new(anchor ? "MATCH.BINARY.EXACT" : "MATCH.METADATA", anchor
            ? CandidateEvidenceStrength.Anchor
            : CandidateEvidenceStrength.Strong)],
        [],
        needsContent);

    private static BookMatchingProfile Profile(
        long id,
        string? language = null,
        string? series = null,
        decimal? seriesIndex = null,
        string author = "Author") => new(
        new(id),
        [$"WORK {id}"],
        ["WORK", id.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        CandidateMetadataNormalizer.AuthorKeys([author]),
        CandidateMetadataNormalizer.AuthorTokens([author]),
        language is null ? [] : [language],
        seriesKey: CandidateMetadataNormalizer.NormalizeSeries(series),
        seriesIndex: seriesIndex);
}
