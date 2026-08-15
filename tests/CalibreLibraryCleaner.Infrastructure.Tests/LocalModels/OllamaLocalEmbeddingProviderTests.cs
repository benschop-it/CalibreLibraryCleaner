using System.Net;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.LocalModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LocalModels;

public sealed class OllamaLocalEmbeddingProviderTests
{
    private const string ModelDigest = "sha256-0800cbac9c2064dde519420e75e512a83cb360de3ad5df176185dc69652fc515";

    [Fact]
    public async Task ConfiguredProviderSendsBoundedBatchAndReturnsVersionedVectors()
    {
        float[][] embeddings =
        [
            Enumerable.Repeat(0.1f, 128).ToArray(),
            Enumerable.Repeat(0.2f, 128).ToArray(),
        ];
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "embeddinggemma:latest",
                embeddings,
            }), Encoding.UTF8, "application/json"),
        });
        using HttpClient client = Client(handler);
        OllamaLocalEmbeddingProvider provider = new(client, Options());
        LocalEmbeddingInput[] inputs =
        [
            new(new(1), "title: Clockwork Harbor | authors: Clara Maker | language: en"),
            new(new(2), "title: Clockwork Harbor Annotated | authors: C. Maker | language: en"),
        ];

        LocalEmbeddingBatchResult result = await provider.EmbedAsync(inputs, CancellationToken.None);

        result.Status.Should().Be(LocalEmbeddingProviderStatus.Available);
        result.Model.Should().Be(new LocalEmbeddingModelIdentity(
            "ollama", "0.32.13", "embeddinggemma:latest", ModelDigest, 128));
        result.Vectors.Should().HaveCount(2);
        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:11434/api/embed"));
        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        request.RootElement.GetProperty("model").GetString().Should().Be("embeddinggemma:latest");
        request.RootElement.GetProperty("truncate").GetBoolean().Should().BeFalse();
        request.RootElement.GetProperty("dimensions").GetInt32().Should().Be(128);
        request.RootElement.GetProperty("input").GetArrayLength().Should().Be(2);
        handler.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task UnconfiguredProviderIsDisabledWithoutHttpRequest()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("HTTP should not run."));
        using HttpClient client = Client(handler);
        OllamaLocalEmbeddingProvider provider = new(client, new());

        LocalEmbeddingBatchResult result = await provider.EmbedAsync(
            [new(new(1), "title: Work | authors: Author | language: en")],
            CancellationToken.None);

        result.Status.Should().Be(LocalEmbeddingProviderStatus.Disabled);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task NonFiniteOrMismatchedResponseFallsBackWithoutVectors()
    {
        string json = """
            {"model":"embeddinggemma:latest","embeddings":[[1e400]]}
            """;
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        using HttpClient client = Client(handler);
        OllamaLocalEmbeddingProvider provider = new(client, Options());

        LocalEmbeddingBatchResult result = await provider.EmbedAsync(
            [new(new(1), "title: Work | authors: Author | language: en")],
            CancellationToken.None);

        result.Status.Should().Be(LocalEmbeddingProviderStatus.Unavailable);
        result.Vectors.Should().BeEmpty();
        result.ProblemCode.Should().Be("OLLAMA.UNAVAILABLE");
    }

    private static OllamaEmbeddingOptions Options() => new(
        "embeddinggemma:latest",
        ModelDigest,
        "0.32.13",
        dimensions: 128,
        requestTimeout: TimeSpan.FromSeconds(2));

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new("http://127.0.0.1:11434/"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) :
        HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }
}
