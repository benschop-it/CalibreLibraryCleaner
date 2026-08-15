using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public sealed record MatchingEvaluationReport(
    string SchemaVersion,
    string CorpusVersion,
    string CalibrationDigest,
    string HoldoutDigest,
    MatchingPolicyIdentities Policies,
    IReadOnlyList<MatchingNamedCount> GenerationTemplates,
    MatchingMetricSlice Overall,
    IReadOnlyList<MatchingMetricSlice> Splits,
    IReadOnlyList<MatchingMetricSlice> Tags,
    IReadOnlyList<MatchingFalseNegative> FalseNegatives,
    IReadOnlyList<MatchingFalsePositive> FalsePositives,
    IReadOnlyList<MatchingKeeperError> KeeperErrors,
    MatchingEvaluationObservations Observations);

public sealed record MatchingPolicyIdentities(
    string ExactMetadata,
    string Matching,
    string Unified,
    string Keeper);

public sealed record MatchingMetricSlice(
    string Name,
    MatchingConfusionMatrix Pairs,
    MatchingRatio Precision,
    MatchingRatio Recall,
    MatchingRatio F1,
    MatchingRatio CandidateRouteRecall,
    MatchingRatio ExpandedCandidateRecall,
    MatchingRatio ExactComponentRate,
    MatchingRatio LanguageAccuracy,
    MatchingRatio KeeperCoverage,
    MatchingRatio KeeperAccuracy,
    int ExpectedGroups,
    int PredictedGroups,
    int OvermergedGroups,
    int FragmentedGroups,
    int CrossLanguageMerges,
    int ContentRequestedPairs,
    int ContentOracleAvailablePairs,
    int ProposedDirectedPairs,
    int RetainedCandidatePairs,
    int RecordsCapped,
    int OrdinaryCapDroppedDirectedPairs,
    int MaximumObservedBucketSize,
    int OversizedBucketOutcomes,
    int GlobalLimitOutcomes,
    IReadOnlyList<MatchingNamedCount> DecisionDispositions,
    IReadOnlyList<MatchingNamedCount> FailureCategories,
    IReadOnlyList<MatchingNamedCount> UnifiedClassifications,
    IReadOnlyList<MatchingNamedCount> UnifiedFindings);

public sealed record MatchingConfusionMatrix(
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    int TrueNegatives);

public sealed record MatchingRatio(int Numerator, int Denominator, decimal? Percentage)
{
    public static MatchingRatio Create(int numerator, int denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numerator);
        ArgumentOutOfRangeException.ThrowIfNegative(denominator);
        if (numerator > denominator) throw new ArgumentException("A metric numerator cannot exceed its denominator.");
        return new(numerator, denominator, denominator == 0
            ? null
            : decimal.Round(numerator * 100m / denominator, 4, MidpointRounding.AwayFromZero));
    }
}

public sealed record MatchingNamedCount(string Name, int Count);

public sealed record MatchingFalseNegative(
    string ScenarioId,
    string FirstRecordId,
    string SecondRecordId,
    string Category);

public sealed record MatchingFalsePositive(
    string ScenarioId,
    string FirstRecordId,
    string SecondRecordId,
    string UnifiedGroupId,
    IReadOnlyList<string> EvidenceCodes,
    IReadOnlyList<string> ContradictionCodes,
    IReadOnlyList<string> FindingCodes);

public sealed record MatchingKeeperError(
    string ScenarioId,
    string WorkKey,
    string Language,
    string ActualRecordId,
    IReadOnlyList<string> AcceptableRecordIds);

public sealed record MatchingEvaluationObservations(
    long ElapsedMilliseconds,
    long AllocatedBytes,
    string RuntimeVersion);

internal sealed record ScenarioMeasurement(
    string ScenarioId,
    MatchingCorpusSplit Split,
    IReadOnlyList<string> Tags,
    MatchingConfusionMatrix Pairs,
    int CandidateRouteReached,
    int CandidateRouteTotal,
    int ExpandedCandidateReached,
    int ExpandedCandidateTotal,
    int ExactComponents,
    int ExpectedGroups,
    int PredictedGroups,
    int OvermergedGroups,
    int FragmentedGroups,
    int CrossLanguageMerges,
    int CorrectLanguages,
    int LanguageTotal,
    int CorrectKeepers,
    int KeeperTotal,
    int ContentRequestedPairs,
    int ContentOracleAvailablePairs,
    int ProposedDirectedPairs,
    int RetainedCandidatePairs,
    int RecordsCapped,
    int OrdinaryCapDroppedDirectedPairs,
    int MaximumObservedBucketSize,
    int OversizedBucketOutcomes,
    int GlobalLimitOutcomes,
    IReadOnlyList<MatchingNamedCount> DecisionDispositions,
    IReadOnlyList<MatchingNamedCount> FailureCategories,
    IReadOnlyList<MatchingNamedCount> UnifiedClassifications,
    IReadOnlyList<MatchingNamedCount> UnifiedFindings,
    IReadOnlyList<MatchingFalseNegative> FalseNegatives,
    IReadOnlyList<MatchingFalsePositive> FalsePositives,
    IReadOnlyList<MatchingKeeperError> KeeperErrors);

internal static class MatchingMetricCalculator
{
    public static MatchingMetricSlice Aggregate(string name, IEnumerable<ScenarioMeasurement> source)
    {
        ScenarioMeasurement[] values = source.ToArray();
        MatchingConfusionMatrix pairs = new(
            values.Sum(value => value.Pairs.TruePositives),
            values.Sum(value => value.Pairs.FalsePositives),
            values.Sum(value => value.Pairs.FalseNegatives),
            values.Sum(value => value.Pairs.TrueNegatives));
        return new(
            name,
            pairs,
            MatchingRatio.Create(pairs.TruePositives, pairs.TruePositives + pairs.FalsePositives),
            MatchingRatio.Create(pairs.TruePositives, pairs.TruePositives + pairs.FalseNegatives),
            MatchingRatio.Create(checked(2 * pairs.TruePositives),
                checked(2 * pairs.TruePositives + pairs.FalsePositives + pairs.FalseNegatives)),
            Ratio(values, value => value.CandidateRouteReached, value => value.CandidateRouteTotal),
            Ratio(values, value => value.ExpandedCandidateReached, value => value.ExpandedCandidateTotal),
            Ratio(values, value => value.ExactComponents, value => value.ExpectedGroups),
            Ratio(values, value => value.CorrectLanguages, value => value.LanguageTotal),
            Ratio(values, value => value.KeeperTotal, value => value.ExpectedGroups),
            Ratio(values, value => value.CorrectKeepers, value => value.KeeperTotal),
            values.Sum(value => value.ExpectedGroups),
            values.Sum(value => value.PredictedGroups),
            values.Sum(value => value.OvermergedGroups),
            values.Sum(value => value.FragmentedGroups),
            values.Sum(value => value.CrossLanguageMerges),
            values.Sum(value => value.ContentRequestedPairs),
            values.Sum(value => value.ContentOracleAvailablePairs),
            values.Sum(value => value.ProposedDirectedPairs),
            values.Sum(value => value.RetainedCandidatePairs),
            values.Sum(value => value.RecordsCapped),
            values.Sum(value => value.OrdinaryCapDroppedDirectedPairs),
            values.Select(value => value.MaximumObservedBucketSize).DefaultIfEmpty().Max(),
            values.Sum(value => value.OversizedBucketOutcomes),
            values.Sum(value => value.GlobalLimitOutcomes),
            AggregateCounts(values.SelectMany(value => value.DecisionDispositions)),
            AggregateCounts(values.SelectMany(value => value.FailureCategories)),
            AggregateCounts(values.SelectMany(value => value.UnifiedClassifications)),
            AggregateCounts(values.SelectMany(value => value.UnifiedFindings)));
    }

    private static MatchingRatio Ratio(
        IEnumerable<ScenarioMeasurement> values,
        Func<ScenarioMeasurement, int> numerator,
        Func<ScenarioMeasurement, int> denominator) =>
        MatchingRatio.Create(values.Sum(numerator), values.Sum(denominator));

    public static IReadOnlyList<MatchingNamedCount> AggregateCounts(IEnumerable<MatchingNamedCount> values) =>
        values.GroupBy(value => value.Name, StringComparer.Ordinal)
            .Select(group => new MatchingNamedCount(group.Key, group.Sum(value => value.Count)))
            .OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
}

public static class MatchingEvaluationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(MatchingEvaluationReport report) => JsonSerializer.Serialize(report, Options);

    public static MatchingEvaluationReport Deserialize(string json) =>
        JsonSerializer.Deserialize<MatchingEvaluationReport>(json, Options)
        ?? throw new MatchingCorpusValidationException("BASELINE.JSON_NULL");
}
