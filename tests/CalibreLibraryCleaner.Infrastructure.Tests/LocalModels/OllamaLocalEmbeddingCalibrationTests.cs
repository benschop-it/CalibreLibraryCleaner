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
            new("P05", true, "Pride and Prejudice", "Jane Austen", "Pride & Prejudice Annotated", "J. Austen"),
            new("P06", true, "Alice's Adventures in Wonderland", "Lewis Carroll", "Alice in Wonderland Illustrated", "L. Carroll"),
            new("P07", true, "The Adventures of Tom Sawyer", "Mark Twain", "Tom Sawyer Unabridged", "M. Twain"),
            new("P08", true, "A Study in Scarlet", "Arthur Conan Doyle", "Study in Scarlet Annotated", "A. C. Doyle"),
            new("P09", true, "The Time Machine", "H. G. Wells", "Time Machine Revised", "H. G. Wells"),
            new("P10", true, "Jane Eyre", "Charlotte Bronte", "Jane Eyre Illustrated", "C. Bronte"),
            new("N01", false, "The Northern Observatory", "Alice Example", "Gardens Beneath Glass", "Alice Example"),
            new("N02", false, "Pride and Prejudice", "Jane Austen", "Emma", "Jane Austen"),
            new("N03", false, "Alice's Adventures in Wonderland", "Lewis Carroll", "Through the Looking-Glass", "Lewis Carroll"),
            new("N04", false, "Clockwork Harbor", "Clara Maker", "Mechanical Orchard", "Clara Maker"),
            new("N05", false, "Sense and Sensibility", "Jane Austen", "Persuasion", "Jane Austen"),
            new("N06", false, "Emma", "Jane Austen", "Mansfield Park", "Jane Austen"),
            new("N07", false, "Alice's Adventures in Wonderland", "Lewis Carroll", "The Hunting of the Snark", "Lewis Carroll"),
            new("N08", false, "Oliver Twist", "Charles Dickens", "Great Expectations", "Charles Dickens"),
            new("N09", false, "A Christmas Carol", "Charles Dickens", "David Copperfield", "Charles Dickens"),
            new("N10", false, "The Adventures of Tom Sawyer", "Mark Twain", "Adventures of Huckleberry Finn", "Mark Twain"),
            new("N11", false, "A Connecticut Yankee in King Arthur's Court", "Mark Twain", "The Prince and the Pauper", "Mark Twain"),
            new("N12", false, "A Study in Scarlet", "Arthur Conan Doyle", "The Hound of the Baskervilles", "Arthur Conan Doyle"),
            new("N13", false, "The Sign of the Four", "Arthur Conan Doyle", "The Valley of Fear", "Arthur Conan Doyle"),
            new("N14", false, "Around the World in Eighty Days", "Jules Verne", "Twenty Thousand Leagues Under the Seas", "Jules Verne"),
            new("N15", false, "Journey to the Center of the Earth", "Jules Verne", "The Mysterious Island", "Jules Verne"),
            new("N16", false, "The Time Machine", "H. G. Wells", "The War of the Worlds", "H. G. Wells"),
            new("N17", false, "The Invisible Man", "H. G. Wells", "The Island of Doctor Moreau", "H. G. Wells"),
            new("N18", false, "Jane Eyre", "Charlotte Bronte", "Villette", "Charlotte Bronte"),
            new("N19", false, "Shirley", "Charlotte Bronte", "The Professor", "Charlotte Bronte"),
            new("N20", false, "Treasure Island", "Robert Louis Stevenson", "Kidnapped", "Robert Louis Stevenson"),
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
        CalibrationObservation[] observations = cases.Select((value, index) =>
        {
            LocalEmbeddingComparison comparison = LocalEmbeddingComparisonPolicy.Compare(
                vectors[new(index * 2 + 1)], vectors[new(index * 2 + 2)]);
            return new CalibrationObservation(
                value.Id,
                value.Positive,
                comparison.SimilarityPermille!.Value);
        }).ToArray();
        int[] positiveScores = observations.Where(value => value.ExpectedPositive)
            .Select(value => value.SimilarityPermille).ToArray();
        int[] negativeScores = observations.Where(value => !value.ExpectedPositive)
            .Select(value => value.SimilarityPermille).ToArray();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = $"ollama/{runtimeVersion}",
            model = modelId,
            modelVersion,
            dimensions = 256,
            preprocessing = LocalEmbeddingInput.PreprocessingVersion,
            comparison = LocalEmbeddingComparison.PolicyVersion,
            positiveMinimum = positiveScores.Min(),
            positiveMaximum = positiveScores.Max(),
            negativeMinimum = negativeScores.Min(),
            negativeMaximum = negativeScores.Max(),
            separableBySingleThreshold = negativeScores.Max() < positiveScores.Min(),
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

    private sealed record CalibrationObservation(
        string CaseId,
        bool ExpectedPositive,
        int SimilarityPermille);
}
