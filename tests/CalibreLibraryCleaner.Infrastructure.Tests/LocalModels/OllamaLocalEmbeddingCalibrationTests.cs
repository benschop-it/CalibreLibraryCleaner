using System.Text.Json;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.LocalModels;
using Xunit;
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LocalModels;

public sealed class OllamaLocalEmbeddingCalibrationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    [Trait("Category", "LocalModelCalibration")]
    public async Task LocalModelCorpusEvaluation()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CALIBRE_RUN_LOCAL_MODEL_EVALUATION"),
            "1", StringComparison.Ordinal))
            return;
        string modelId = Environment.GetEnvironmentVariable("CALIBRE_OLLAMA_EMBEDDING_MODEL")
            ?? throw new InvalidOperationException("A local embedding model must be configured.");
        string modelVersion = Environment.GetEnvironmentVariable("CALIBRE_OLLAMA_EMBEDDING_MODEL_VERSION")
            ?? throw new InvalidOperationException("An immutable local model version must be configured.");
        string runtimeVersion = Environment.GetEnvironmentVariable("CALIBRE_OLLAMA_RUNTIME_VERSION")
            ?? throw new InvalidOperationException("The local Ollama runtime version must be configured.");
        OllamaEmbeddingOptions options = new(modelId, modelVersion, runtimeVersion, dimensions: 256);
        using HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        OllamaLocalEmbeddingProvider provider = new(client, options);
        CalibrationCase[] cases =
        [
            new("P01", true, "Clockwork Harbor", "Clara Maker", "Clockwork Harbor Annotated", "C. Maker"),
            new("P02", true, "Mechanical Orchard", "Rosa Maker", "Mechanical Orchard Annotated", "R. Maker"),
            new("P03", true, "The Northern Observatory", "Alice Example", "Northern Observatory Illustrated", "A. Example"),
            new("P04", true, "Gardens Beneath Glass", "Beatrice Writer", "Gardens Under Glass Revised", "B. Writer"),
            new("N01", false, "The Northern Observatory", "Alice Example", "Gardens Beneath Glass", "Alice Example"),
            new("N02", false, "Pride and Prejudice", "Jane Austen", "Emma", "Jane Austen"),
            new("N03", false, "Alice's Adventures in Wonderland", "Lewis Carroll", "Through the Looking-Glass", "Lewis Carroll"),
            new("N04", false, "Clockwork Harbor", "Clara Maker", "Mechanical Orchard", "Clara Maker"),
        ];
        LocalEmbeddingInput[] inputs = cases.SelectMany((value, index) => new[]
        {
            Input(index * 2 + 1, value.FirstTitle, value.FirstAuthor),
            Input(index * 2 + 2, value.SecondTitle, value.SecondAuthor),
        }).ToArray();
        Dictionary<CalibreBookId, LocalEmbeddingVector> vectors = [];
        foreach (LocalEmbeddingInput[] batch in inputs.Chunk(8))
        {
            LocalEmbeddingBatchResult result = await provider.EmbedAsync(batch, CancellationToken.None);
            if (result.Status != LocalEmbeddingProviderStatus.Available)
                throw new InvalidOperationException(result.ProblemCode ?? "Local model unavailable.");
            foreach ((CalibreBookId id, LocalEmbeddingVector vector) in result.Vectors) vectors[id] = vector;
        }
        object[] observations = cases.Select((value, index) =>
        {
            LocalEmbeddingComparison comparison = LocalEmbeddingComparisonPolicy.Compare(
                vectors[new(index * 2 + 1)], vectors[new(index * 2 + 2)]);
            return (object)new
            {
                caseId = value.Id,
                expectedPositive = value.Positive,
                similarityPermille = comparison.SimilarityPermille,
            };
        }).ToArray();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = $"ollama/{runtimeVersion}",
            model = modelId,
            modelVersion,
            dimensions = 256,
            preprocessing = LocalEmbeddingInput.PreprocessingVersion,
            comparison = LocalEmbeddingComparison.PolicyVersion,
            observations,
        }, JsonOptions));
    }

    private static LocalEmbeddingInput Input(long id, string title, string author)
    {
        CalibreBook book = new(
            new(id), title, author, [new(null, author, author)], [], [], $"Book {id}",
            new(languages: ["eng"]));
        BookMatchingProfile profile = BookMatchingProfileFactory.Create([book]).Single();
        return LocalEmbeddingInput.Create(book, profile);
    }

    private sealed record CalibrationCase(
        string Id,
        bool Positive,
        string FirstTitle,
        string FirstAuthor,
        string SecondTitle,
        string SecondAuthor);
}
