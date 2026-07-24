using CalibreLibraryCleaner.Domain.Recommendations;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Recommendations;

public sealed class RecommendationReviewValueTests
{
    [Fact]
    public void VersionsConfidenceAndScoresRemainDifferentTypes()
    {
        RecommendationModelVersion.V1.Value.Should().Be("consolidation-recommendation/1.0.2");
        Enum.GetNames<RecommendationConfidence>().Should().Contain(["ManualReviewRequired", "Unsupported"]);
        typeof(RecommendationConfidence).Should().NotBe<Domain.Assessments.QualityScore>();
    }

    [Fact]
    public void OverrideCanonicalizesActionsWithoutMutatingGeneratedState()
    {
        UserRecommendationOverride value = new(
            RecommendationModelVersion.V1,
            new("input"),
            RecommendationReviewStatus.ManuallyAdjusted,
            DateTimeOffset.UnixEpoch,
            formatOverrides: [new("epub", FormatOverrideAction.MarkUnresolved)]);

        value.FormatOverrides.Single().Format.Should().Be("EPUB");
        value.ReviewedAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }
}
