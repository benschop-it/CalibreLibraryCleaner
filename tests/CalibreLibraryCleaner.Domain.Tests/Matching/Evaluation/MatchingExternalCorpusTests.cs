using Xunit;
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public sealed class MatchingExternalCorpusTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "LocalMatchingEvaluation")]
    public void EvaluateExplicitExternalCorpusWithoutMetadataOutput()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CALIBRE_RUN_EXTERNAL_MATCHING_EVALUATION"),
            "1", StringComparison.Ordinal))
            return;
        string? configuredPath = Environment.GetEnvironmentVariable("CALIBRE_MATCHING_CORPUS_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new MatchingCorpusValidationException("CORPUS.EXTERNAL_DIRECTORY_INVALID");
        if (!Directory.Exists(configuredPath))
            throw new MatchingCorpusValidationException("CORPUS.EXTERNAL_DIRECTORY_INVALID");

        MatchingCorpus calibration = MatchingCorpusLoader.LoadExternal(
            Path.Combine(configuredPath, "calibration.v1.json"));
        MatchingCorpus holdout = MatchingCorpusLoader.LoadExternal(
            Path.Combine(configuredPath, "holdout.v1.json"));
        MatchingEvaluationReport report = MatchingCorpusEvaluator.Evaluate(calibration, holdout) with
        {
            Observations = new(0, 0, Environment.Version.ToString()),
        };

        output.WriteLine(MatchingEvaluationJson.Serialize(report));
    }
}
