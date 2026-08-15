using System.Text;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public sealed class MatchingCorpusEvaluationTests
{
    [Fact]
    public void StrictLoaderRejectsUnknownFieldsWithoutEchoingMetadata()
    {
        const string secret = "private-book-title";
        string json = $$"""
            {
              "schemaVersion": "matching-corpus/1.0",
              "corpusVersion": "matching-corpus-data/1.0",
              "split": "Calibration",
              "source": { "id": "synthetic", "provenance": "Generated", "license": "CC0-1.0" },
              "scenarios": [],
              "privateTitle": "{{secret}}"
            }
            """;

        Action action = () => MatchingCorpusLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        MatchingCorpusValidationException exception = action.Should()
            .Throw<MatchingCorpusValidationException>().Which;
        exception.Code.Should().Be("CORPUS.JSON_INVALID");
        exception.Message.Should().NotContain(secret);
    }

    [Fact]
    public void CanonicalDigestIgnoresScenarioRecordAndTagOrder()
    {
        MatchingCorpus forward = Corpus(MatchingCorpusSplit.Calibration, "cal", "work-cal");
        MatchingScenario scenario = forward.Scenarios.Single();
        MatchingCorpus reversed = forward with
        {
            Scenarios =
            [
                scenario with
                {
                    Tags = scenario.Tags.Reverse().ToArray(),
                    Records = scenario.Records.Reverse().ToArray(),
                },
            ],
        };

        MatchingCorpusLoader.CanonicalDigest(reversed).Should().Be(MatchingCorpusLoader.CanonicalDigest(forward));
    }

    [Fact]
    public void SplitValidatorRejectsSharedWorkWithoutDisclosingMetadata()
    {
        MatchingCorpus calibration = Corpus(MatchingCorpusSplit.Calibration, "cal", "shared-work");
        MatchingCorpus holdout = Corpus(MatchingCorpusSplit.Holdout, "hold", "shared-work");

        Action action = () => MatchingCorpusLoader.ValidateSplits(calibration, holdout);

        action.Should().Throw<MatchingCorpusValidationException>()
            .Which.Code.Should().Be("CORPUS.WORK_SPLIT_LEAKAGE");
    }

    [Fact]
    public void RatiosKeepUndefinedValuesExplicit()
    {
        MatchingRatio.Create(0, 0).Should().Be(new MatchingRatio(0, 0, null));
        MatchingRatio.Create(2, 3).Should().Be(new MatchingRatio(2, 3, 66.6667m));
    }

    [Fact]
    public void ValidationRejectsUnknownTagsWithOnlyOpaqueContext()
    {
        MatchingCorpus corpus = Corpus(MatchingCorpusSplit.Calibration, "cal", "work-cal");
        MatchingScenario scenario = corpus.Scenarios.Single() with { Tags = ["secret-title-value"] };

        Action action = () => MatchingCorpusLoader.CanonicalDigest(corpus with { Scenarios = [scenario] });

        MatchingCorpusValidationException exception = action.Should()
            .Throw<MatchingCorpusValidationException>().Which;
        exception.Code.Should().Be("CORPUS.TAGS_INVALID");
        exception.Message.Should().Be("CORPUS.TAGS_INVALID;scenario=cal-scenario");
        exception.Message.Should().NotContain("secret-title-value");
    }

    [Fact]
    public void ValidationRejectsRootedPathsAndInvalidContentEvidence()
    {
        MatchingCorpus corpus = Corpus(MatchingCorpusSplit.Calibration, "cal", "work-cal");
        MatchingScenario scenario = corpus.Scenarios.Single();
        MatchingRecordFixture record = scenario.Records[0] with
        {
            Formats = [new("EPUB", "private", "C:/Private/book.epub", null, null)],
        };
        MatchingCorpus rooted = corpus with
        {
            Scenarios = [scenario with { Records = [record, scenario.Records[1]] }],
        };

        Action pathAction = () => MatchingCorpusLoader.CanonicalDigest(rooted);

        MatchingCorpusValidationException pathException = pathAction.Should()
            .Throw<MatchingCorpusValidationException>().Which;
        pathException.Code.Should().Be("CORPUS.PATH_INVALID");
        pathException.Message.Should().NotContain("Private");

        MatchingContentOracle invalid = new("cal-a", "cal-b", "EquivalentText", 13, 0, 0, 0, 0, 0, 0);
        MatchingCorpus invalidContent = corpus with
        {
            Scenarios = [scenario with { ContentComparisons = [invalid] }],
        };
        Action contentAction = () => MatchingCorpusLoader.CanonicalDigest(invalidContent);
        contentAction.Should().Throw<MatchingCorpusValidationException>()
            .Which.Code.Should().Be("CORPUS.CONTENT_EVIDENCE_INVALID");
    }

    [Fact]
    public void ExternalLoaderRejectsOversizedFileBeforeMaterialization()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"matching-corpus-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "oversized.json");
        Directory.CreateDirectory(directory);
        try
        {
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(MatchingCorpusLoader.MaximumDocumentBytes + 1L);

            Action action = () => MatchingCorpusLoader.LoadExternal(path);

            action.Should().Throw<MatchingCorpusValidationException>()
                .Which.Code.Should().Be("CORPUS.DOCUMENT_TOO_LARGE");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EvaluatorRunsExactProfilesCandidatesAndUnifiedMergeEndToEnd()
    {
        MatchingCorpus calibration = Corpus(MatchingCorpusSplit.Calibration, "cal", "work-cal");
        MatchingCorpus holdout = Corpus(MatchingCorpusSplit.Holdout, "hold", "work-hold");

        MatchingEvaluationReport report = MatchingCorpusEvaluator.Evaluate(calibration, holdout);

        report.Overall.Pairs.Should().Be(new MatchingConfusionMatrix(2, 0, 0, 0));
        report.Overall.Precision.Percentage.Should().Be(100m);
        report.Overall.Recall.Percentage.Should().Be(100m);
        report.Overall.CandidateRouteRecall.Percentage.Should().Be(100m);
        report.Overall.ExactComponentRate.Percentage.Should().Be(100m);
        report.FalseNegatives.Should().BeEmpty();
        report.FalsePositives.Should().BeEmpty();
        report.KeeperErrors.Should().BeEmpty();
    }

    private static MatchingCorpus Corpus(MatchingCorpusSplit split, string prefix, string workKey)
    {
        MatchingRecordFixture first = Record($"{prefix}-a", split == MatchingCorpusSplit.Calibration ? 1 : 101, workKey);
        MatchingRecordFixture second = Record($"{prefix}-b", split == MatchingCorpusSplit.Calibration ? 2 : 102, workKey);
        MatchingScenario scenario = new(
            $"{prefix}-scenario",
            ["exact-metadata", "unicode-punctuation"],
            $"{prefix}-source-family",
            "exact-pair",
            "1.0",
            [first, second],
            [],
            [new(workKey, "en", [first.Key])],
            "Synthetic exact metadata pair.");
        return new(
            MatchingCorpusVocabulary.SchemaVersion,
            "matching-corpus-data/1.0",
            split,
            new($"{prefix}-source", "Generated synthetic fixtures", "CC0-1.0"),
            [scenario]);
    }

    private static MatchingRecordFixture Record(string key, long id, string workKey) => new(
        key,
        id,
        "The Synthetic Observatory",
        "Example, Alice",
        [new("Alice Example", "Example, Alice")],
        [],
        new(null, null, null, null, ["en"], false),
        [],
        null,
        workKey,
        "en");
}
