using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public sealed class MatchingCorpusRegressionTests
{
    [Fact]
    public void CommittedCorporaMeetCoverageAndSplitRequirements()
    {
        (MatchingCorpus calibration, MatchingCorpus holdout) = LoadCorpora();
        MatchingCorpusLoader.ValidateSplits(calibration, holdout);
        MatchingScenario[] scenarios = [.. calibration.Scenarios, .. holdout.Scenarios];
        MatchingRecordFixture[] records = scenarios.SelectMany(value => value.Records).ToArray();
        int workFamilies = records.Select(value => value.WorkKey).Distinct(StringComparer.Ordinal).Count();
        int holdoutFamilies = holdout.Scenarios.SelectMany(value => value.Records)
            .Select(value => value.WorkKey).Distinct(StringComparer.Ordinal).Count();
        (int positives, int negatives) = PairCounts(scenarios);

        workFamilies.Should().BeGreaterThanOrEqualTo(100);
        records.Should().HaveCountGreaterThanOrEqualTo(200);
        positives.Should().BeGreaterThanOrEqualTo(100);
        negatives.Should().BeGreaterThanOrEqualTo(250);
        (holdoutFamilies * 100).Should().BeGreaterThanOrEqualTo(workFamilies * 30);
        foreach (string tag in MatchingCorpusVocabulary.Tags)
            scenarios.Count(value => value.Tags.Contains(tag, StringComparer.Ordinal))
                .Should().BeGreaterThanOrEqualTo(5, $"tag {tag} requires five reviewed scenarios");
    }

    [Fact]
    public void CommittedCorporaRunTheCurrentPipelineAndObserveCandidateCaps()
    {
        (MatchingCorpus calibration, MatchingCorpus holdout) = LoadCorpora();

        MatchingEvaluationReport report = MatchingCorpusEvaluator.Evaluate(calibration, holdout);

        report.Overall.RecordsCapped.Should().BeGreaterThan(0);
        report.Overall.OrdinaryCapDroppedDirectedPairs.Should().BeGreaterThan(0);
        report.Overall.MaximumObservedBucketSize.Should().BeGreaterThanOrEqualTo(22);
        report.Overall.OversizedBucketOutcomes.Should().Be(0);
        report.GenerationTemplates.Should().Contain(value => value.Name == "candidate-cap" && value.Count == 10);
        report.Overall.CandidateRouteRecall.Denominator.Should().BeGreaterThanOrEqualTo(100);
        report.Overall.KeeperCoverage.Should().Be(new MatchingRatio(190, 212, 89.6226m));
        (report.Overall.LanguageAccuracy.Denominator - report.Overall.LanguageAccuracy.Numerator)
            .Should().Be(report.Overall.CrossLanguageMerges);
        report.Overall.FailureCategories.Should().NotContain(value => value.Name == "UNKNOWN_PIPELINE_GAP");
    }

    [Fact]
    public void ReversedCorpusScenarioRecordAndContentOrderHasIdenticalSemanticReport()
    {
        (MatchingCorpus calibration, MatchingCorpus holdout) = LoadCorpora();
        MatchingEvaluationReport forward = MatchingCorpusEvaluator.Evaluate(calibration, holdout);
        MatchingEvaluationReport reverse = MatchingCorpusEvaluator.Evaluate(Reverse(calibration), Reverse(holdout));

        SemanticJson(reverse).Should().Be(SemanticJson(forward));
    }

    [Fact]
    public void CurrentEvaluationMatchesReviewedSemanticBaseline()
    {
        (MatchingCorpus calibration, MatchingCorpus holdout) = LoadCorpora();
        MatchingEvaluationReport current = MatchingCorpusEvaluator.Evaluate(calibration, holdout);
        MatchingEvaluationReport baseline = MatchingEvaluationJson.Deserialize(
            MatchingCorpusResources.LoadText("baseline.v1.json"));

        current.FalseNegatives.Should().NotContain(value => value.Category == "UNKNOWN_PIPELINE_GAP");
        current.Should().BeEquivalentTo(
            baseline,
            options => options.Excluding(value => value.Observations).WithStrictOrdering());
    }

    [Fact]
    public void DomainEvaluationProjectDoesNotReferenceApplicationOrInfrastructure()
    {
        string[] references = typeof(MatchingCorpusEvaluator).Assembly.GetReferencedAssemblies()
            .Select(value => value.Name ?? string.Empty).ToArray();

        references.Should().NotContain("CalibreLibraryCleaner.Application");
        references.Should().NotContain("CalibreLibraryCleaner.Infrastructure");
    }

    private static (MatchingCorpus Calibration, MatchingCorpus Holdout) LoadCorpora() =>
        (MatchingCorpusResources.LoadCorpus("calibration.v1.json"),
            MatchingCorpusResources.LoadCorpus("holdout.v1.json"));

    private static (int Positives, int Negatives) PairCounts(IEnumerable<MatchingScenario> scenarios)
    {
        int positives = 0;
        int negatives = 0;
        foreach (MatchingScenario scenario in scenarios)
        {
            for (int first = 0; first < scenario.Records.Count; first++)
                for (int second = first + 1; second < scenario.Records.Count; second++)
                {
                    MatchingRecordFixture left = scenario.Records[first];
                    MatchingRecordFixture right = scenario.Records[second];
                    if (left.WorkKey == right.WorkKey && left.ExpectedLanguage == right.ExpectedLanguage)
                        positives++;
                    else
                        negatives++;
                }
        }
        return (positives, negatives);
    }

    private static MatchingCorpus Reverse(MatchingCorpus corpus) => corpus with
    {
        Scenarios = corpus.Scenarios.Reverse().Select(scenario => scenario with
        {
            Tags = scenario.Tags.Reverse().ToArray(),
            Records = scenario.Records.Reverse().ToArray(),
            ContentComparisons = scenario.ContentComparisons.Reverse().ToArray(),
            ExpectedGroups = scenario.ExpectedGroups.Reverse().ToArray(),
        }).ToArray(),
    };

    private static string SemanticJson(MatchingEvaluationReport report) =>
        MatchingEvaluationJson.Serialize(report with
        {
            Observations = new(0, 0, string.Empty),
        });
}
