using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Infrastructure.LocalModels;

internal sealed class OllamaLocalEmbeddingProvider(
    HttpClient httpClient,
    OllamaEmbeddingOptions options) : ILocalEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
    };

    public bool Enabled => options.Enabled;

    public async Task<LocalEmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<LocalEmbeddingInput> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!Enabled)
            return new(LocalEmbeddingProviderStatus.Disabled, null, problemCode: "OLLAMA.DISABLED");
        if (inputs.Count is 0 || inputs.Count > options.MaximumBatchSize
            || inputs.Select(value => value.BookId).Distinct().Count() != inputs.Count)
            throw new ArgumentException("Ollama embedding batch is invalid.", nameof(inputs));
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "api/embed")
            {
                Content = JsonContent.Create(new EmbedRequest(
                    options.ModelId!,
                    inputs.Select(value => value.Text).ToArray(),
                    false,
                    options.Dimensions,
                    "5m"), options: JsonOptions),
            };
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Unavailable("OLLAMA.MODEL_OR_ENDPOINT_NOT_FOUND");
            if (!response.IsSuccessStatusCode)
                return Unavailable($"OLLAMA.HTTP_{(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
                return Unavailable("OLLAMA.RESPONSE_TOO_LARGE");
            byte[] bytes = await ReadBoundedAsync(
                response.Content, options.MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            EmbedResponse? document = JsonSerializer.Deserialize<EmbedResponse>(bytes, JsonOptions);
            if (document is not { Embeddings: { } embeddings }
                || document.Model != options.ModelId
                || embeddings.Length != inputs.Count)
                return Unavailable("OLLAMA.RESPONSE_INVALID");
            LocalEmbeddingModelIdentity model = new(
                "ollama",
                options.RuntimeVersion!,
                options.ModelId!,
                options.ModelVersion!,
                options.Dimensions);
            Dictionary<CalibreLibraryCleaner.Domain.Libraries.CalibreBookId, LocalEmbeddingVector> vectors = [];
            for (int index = 0; index < inputs.Count; index++)
            {
                LocalEmbeddingInput input = inputs[index];
                vectors[input.BookId] = new(
                    input.BookId,
                    input.InputIdentity,
                    model,
                    embeddings[index]);
            }
            return new(LocalEmbeddingProviderStatus.Available, model, vectors);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("OLLAMA.TIMEOUT");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException or ArgumentException or OverflowException)
        {
            return Unavailable("OLLAMA.UNAVAILABLE");
        }

        LocalEmbeddingBatchResult Unavailable(string problemCode) => new(
            LocalEmbeddingProviderStatus.Unavailable, null, problemCode: problemCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] block = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Ollama response is too large.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input,
        [property: JsonPropertyName("truncate")] bool Truncate,
        [property: JsonPropertyName("dimensions")] int Dimensions,
        [property: JsonPropertyName("keep_alive")] string KeepAlive);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("embeddings")] float[][]? Embeddings);
}
