using Xunit;
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public sealed class MatchingBaselineGenerationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "MatchingBaselineGeneration")]
    public void EmitCanonicalBaselineForExplicitReview()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CALIBRE_EMIT_MATCHING_BASELINE"),
            "1", StringComparison.Ordinal))
            return;

        MatchingCorpus calibration = MatchingCorpusResources.LoadCorpus("calibration.v1.json");
        MatchingCorpus holdout = MatchingCorpusResources.LoadCorpus("holdout.v1.json");
        MatchingEvaluationReport report = MatchingCorpusEvaluator.Evaluate(calibration, holdout) with
        {
            Observations = new(0, 0, Environment.Version.ToString()),
        };

        string json = MatchingEvaluationJson.Serialize(report);
        string? requestedPath = Environment.GetEnvironmentVariable("CALIBRE_MATCHING_REPORT_PATH");
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            string destination = Path.GetFullPath(requestedPath);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!destination.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new MatchingCorpusValidationException("BASELINE.OUTPUT_PATH_INVALID");
            string? directory = Path.GetDirectoryName(destination);
            if (directory is null) throw new MatchingCorpusValidationException("BASELINE.OUTPUT_PATH_INVALID");
            Directory.CreateDirectory(directory);
            string staging = destination + ".tmp";
            File.WriteAllText(staging, json);
            File.Move(staging, destination, overwrite: true);
        }

        output.WriteLine(json);
    }
}
