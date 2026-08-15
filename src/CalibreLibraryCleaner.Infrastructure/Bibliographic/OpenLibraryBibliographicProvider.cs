using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

internal sealed class OpenLibraryBibliographicProvider(
    HttpClient httpClient,
    OpenLibraryOptions options) : IBibliographicProvider, IDisposable
{
    private const string ProviderVersion = "open-library-search-1.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private DateTimeOffset _nextRequestUtc = DateTimeOffset.MinValue;
    private int _disposed;

    public BibliographicProviderIdentity Identity { get; } = new("open-library", ProviderVersion);

    public async Task<BibliographicSearchResult> SearchAsync(
        BibliographicLookupQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Provider != Identity)
            throw new ArgumentException("The query targets another bibliographic provider.", nameof(query));
        if (!options.Enabled)
            return Unavailable("OPEN_LIBRARY.DISABLED");

        await WaitForRateSlotAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, BuildRelativeUri(query));
            string userAgent = "CalibreLibraryCleaner/1.0 (+https://github.com/benschop-it/CalibreLibraryCleaner)";
            if (options.Contact is not null) userAgent += $" ({options.Contact})";
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(Identity, BibliographicSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "OPEN_LIBRARY.NOT_FOUND");
            if (!response.IsSuccessStatusCode)
                return Unavailable($"OPEN_LIBRARY.HTTP_{(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
                return Unavailable("OPEN_LIBRARY.RESPONSE_TOO_LARGE");

            byte[] bytes = await ReadBoundedAsync(
                response.Content, options.MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            OpenLibraryDocument? document = JsonSerializer.Deserialize<OpenLibraryDocument>(bytes, JsonOptions);
            BibliographicWorkCandidate[] candidates = (document?.Docs ?? [])
                .Take(5)
                .Where(value => IsWorkId(value.Key)
                    && !string.IsNullOrWhiteSpace(value.Title)
                    && value.AuthorName is { Length: > 0 })
                .Select(value => new BibliographicWorkCandidate(
                    value.Key!,
                    value.Title!,
                    value.AuthorName!,
                    value.Language,
                    Math.Max(0, value.EditionCount)))
                .ToArray();
            return candidates.Length == 0
                ? new(Identity, BibliographicSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_RESULTS")
                : new(Identity, BibliographicSearchStatus.Success, DateTimeOffset.UtcNow, candidates);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("OPEN_LIBRARY.TIMEOUT");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException or ArgumentException or OverflowException)
        {
            return Unavailable("OPEN_LIBRARY.UNAVAILABLE");
        }

        BibliographicSearchResult Unavailable(string problem) => new(
            Identity, BibliographicSearchStatus.Unavailable, DateTimeOffset.UtcNow, problemCode: problem);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _rateGate.Dispose();
    }

    private async Task WaitForRateSlotAsync(CancellationToken cancellationToken)
    {
        await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan delay = _nextRequestUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _nextRequestUtc = DateTimeOffset.UtcNow + options.MinimumRequestInterval;
        }
        finally
        {
            _rateGate.Release();
        }
    }

    private static string BuildRelativeUri(BibliographicLookupQuery query)
    {
        List<string> parameters = [];
        if (query.Identifier is not null)
            parameters.Add("isbn=" + Uri.EscapeDataString(query.Identifier[5..]));
        else
        {
            parameters.Add("title=" + Uri.EscapeDataString(query.Title!));
            parameters.Add("author=" + Uri.EscapeDataString(string.Join("; ", query.Authors)));
            if (query.Language is not null) parameters.Add("lang=" + Uri.EscapeDataString(query.Language));
        }
        parameters.Add("fields=key%2Ctitle%2Cauthor_name%2Clanguage%2Cedition_count");
        parameters.Add("limit=5");
        return "search.json?" + string.Join('&', parameters);
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
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Provider response is too large.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool IsWorkId(string? value) => value is { Length: >= 12 and <= 160 }
        && value.StartsWith("/works/OL", StringComparison.Ordinal)
        && value.EndsWith('W')
        && value[9..^1].All(char.IsAsciiDigit);

    private sealed record OpenLibraryDocument(
        [property: JsonPropertyName("docs")] OpenLibraryWork[]? Docs);

    private sealed record OpenLibraryWork(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("author_name")] string[]? AuthorName,
        [property: JsonPropertyName("language")] string[]? Language,
        [property: JsonPropertyName("edition_count")] int EditionCount);
}
