using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

internal sealed class GoogleBooksEditionMetadataProvider(
    HttpClient httpClient,
    IGoogleBooksApiKeyStore apiKeyStore,
    GoogleBooksOptions options) : IEditionMetadataProvider, IEditionMetadataProviderAvailability, IDisposable
{
    private const string ProviderVersion = "google-books-volume-search/1.0.0";
    private const string ResponseFields = "items(id,volumeInfo(title,subtitle,authors,publisher,publishedDate,industryIdentifiers,language)),totalItems";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 12,
    };
    private int _disposed;

    public EditionMetadataProviderIdentity Identity { get; } = new("google-books", ProviderVersion);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        await apiKeyStore.IsConfiguredAsync(cancellationToken).ConfigureAwait(false);

    public async Task<EditionMetadataSearchResult> SearchAsync(
        EditionMetadataLookupQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Provider != Identity)
            throw new ArgumentException("The query targets another edition metadata provider.", nameof(query));
        string? apiKey;
        try
        {
            apiKey = await apiKeyStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or InvalidOperationException or ArgumentException)
        {
            return Unavailable("GOOGLE_BOOKS.CREDENTIAL_UNAVAILABLE");
        }
        if (apiKey is null) return Unavailable("GOOGLE_BOOKS.API_KEY_NOT_CONFIGURED");

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, BuildRelativeUri(query, apiKey));
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(Identity, EditionMetadataSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "GOOGLE_BOOKS.NOT_FOUND");
            if (!response.IsSuccessStatusCode)
                return Unavailable($"GOOGLE_BOOKS.HTTP_{(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
                return Unavailable("GOOGLE_BOOKS.RESPONSE_TOO_LARGE");
            byte[] bytes = await ReadBoundedAsync(
                response.Content, options.MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            GoogleBooksDocument? document = JsonSerializer.Deserialize<GoogleBooksDocument>(bytes, JsonOptions);
            EditionMetadataCandidate[] candidates = (document?.Items ?? [])
                .Take(options.MaximumResults)
                .Select(TryCreateCandidate)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToArray();
            return candidates.Length == 0
                ? new(Identity, EditionMetadataSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "GOOGLE_BOOKS.NO_COMPATIBLE_VOLUMES")
                : new(Identity, EditionMetadataSearchStatus.Success, DateTimeOffset.UtcNow, candidates);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("GOOGLE_BOOKS.TIMEOUT");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException or ArgumentException or OverflowException)
        {
            return Unavailable("GOOGLE_BOOKS.UNAVAILABLE");
        }

        EditionMetadataSearchResult Unavailable(string problem) => new(
            Identity, EditionMetadataSearchStatus.Unavailable, DateTimeOffset.UtcNow,
            problemCode: problem);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) httpClient.Dispose();
    }

    private string BuildRelativeUri(EditionMetadataLookupQuery query, string apiKey)
    {
        string search = query.Identifier is not null
            ? "isbn:" + query.Identifier[5..]
            : $"intitle:\"{SearchValue(query.Title!)}\" inauthor:\"{SearchValue(string.Join("; ", query.Authors))}\"";
        List<string> parameters =
        [
            "q=" + Uri.EscapeDataString(search),
            "maxResults=" + options.MaximumResults.ToString(CultureInfo.InvariantCulture),
            "orderBy=relevance",
            "printType=books",
            "projection=lite",
            "fields=" + Uri.EscapeDataString(ResponseFields),
        ];
        if (query.Language is not null)
            parameters.Add("langRestrict=" + Uri.EscapeDataString(query.Language));
        parameters.Add("key=" + Uri.EscapeDataString(apiKey));
        return "volumes?" + string.Join('&', parameters);
    }

    private static string SearchValue(string value) => value.Replace('"', ' ').Replace('\\', ' ').Trim();

    private static EditionMetadataCandidate? TryCreateCandidate(GoogleBooksVolume value)
    {
        GoogleBooksVolumeInfo? info = value.VolumeInfo;
        if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 128
            || value.Id.Any(char.IsControl) || string.IsNullOrWhiteSpace(info?.Title)
            || info.Authors is not { Length: > 0 })
            return null;
        try
        {
            EditionMetadataIdentifier[] identifiers = (info.IndustryIdentifiers ?? [])
                .Select(TryCreateIdentifier).Where(identifier => identifier is not null)
                .Select(identifier => identifier!).Distinct().Take(16).ToArray();
            string title = string.IsNullOrWhiteSpace(info.Subtitle)
                ? info.Title!
                : $"{info.Title}: {info.Subtitle}";
            string sourceId = "google-books-volume:" + value.Id;
            return new(
                sourceId,
                sourceId,
                title,
                info.Authors!,
                identifiers,
                info.Publisher,
                ParsePublicationDate(info.PublishedDate),
                info.Language is null ? null : [info.Language]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static EditionMetadataIdentifier? TryCreateIdentifier(GoogleBooksIdentifier value)
    {
        if (value.Type is not ("ISBN_10" or "ISBN_13") || string.IsNullOrWhiteSpace(value.Identifier))
            return null;
        try { return new("isbn", value.Identifier); }
        catch (ArgumentException) { return null; }
    }

    private static EditionPublicationDate? ParsePublicationDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        if (DateTime.TryParseExact(normalized, "yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime year))
            return new(year.Year);
        if (DateTime.TryParseExact(normalized, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime month))
            return new(month.Year, month.Month);
        return DateTime.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime day)
            ? new(day.Year, day.Month, day.Day)
            : null;
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
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("Provider response is too large.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record GoogleBooksDocument(
        [property: JsonPropertyName("items")] GoogleBooksVolume[]? Items);

    private sealed record GoogleBooksVolume(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("volumeInfo")] GoogleBooksVolumeInfo? VolumeInfo);

    private sealed record GoogleBooksVolumeInfo(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("subtitle")] string? Subtitle,
        [property: JsonPropertyName("authors")] string[]? Authors,
        [property: JsonPropertyName("publisher")] string? Publisher,
        [property: JsonPropertyName("publishedDate")] string? PublishedDate,
        [property: JsonPropertyName("industryIdentifiers")] GoogleBooksIdentifier[]? IndustryIdentifiers,
        [property: JsonPropertyName("language")] string? Language);

    private sealed record GoogleBooksIdentifier(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("identifier")] string? Identifier);
}
