using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

internal sealed class OpenLibraryBibliographicProvider(
    HttpClient httpClient,
    OpenLibraryOptions options) : IBibliographicProvider, IEditionMetadataProvider, IDisposable
{
    private const string ProviderVersion = "open-library-search-1.0.0";
    private const string EditionProviderVersion = "open-library-edition-search/1.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private DateTimeOffset _nextRequestUtc = DateTimeOffset.MinValue;
    private int _disposed;

    public BibliographicProviderIdentity Identity { get; } = new("open-library", ProviderVersion);

    EditionMetadataProviderIdentity IEditionMetadataProvider.Identity { get; } = new(
        "open-library", EditionProviderVersion);

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

    public async Task<EditionMetadataSearchResult> SearchAsync(
        EditionMetadataLookupQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        EditionMetadataProviderIdentity editionIdentity = ((IEditionMetadataProvider)this).Identity;
        if (query.Provider != editionIdentity)
            throw new ArgumentException("The query targets another edition metadata provider.", nameof(query));
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
                return new(editionIdentity, EditionMetadataSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "OPEN_LIBRARY.NOT_FOUND");
            if (!response.IsSuccessStatusCode)
                return Unavailable($"OPEN_LIBRARY.HTTP_{(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
                return Unavailable("OPEN_LIBRARY.RESPONSE_TOO_LARGE");

            byte[] bytes = await ReadBoundedAsync(
                response.Content, options.MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            OpenLibraryEditionSearchDocument? document = JsonSerializer.Deserialize<OpenLibraryEditionSearchDocument>(
                bytes, JsonOptions);
            EditionMetadataCandidate[] candidates = (document?.Docs ?? [])
                .Take(5)
                .Select(TryCreateEditionCandidate)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToArray();
            return candidates.Length == 0
                ? new(editionIdentity, EditionMetadataSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS")
                : new(editionIdentity, EditionMetadataSearchStatus.Success, DateTimeOffset.UtcNow, candidates);
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

        EditionMetadataSearchResult Unavailable(string problem) => new(
            editionIdentity, EditionMetadataSearchStatus.Unavailable, DateTimeOffset.UtcNow,
            problemCode: problem);
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

    private static string BuildRelativeUri(EditionMetadataLookupQuery query)
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
        parameters.Add("fields=key%2Ctitle%2Cauthor_name%2Ceditions%2Ceditions.key%2Ceditions.title%2Ceditions.language%2Ceditions.isbn%2Ceditions.publisher%2Ceditions.publish_date%2Ceditions.cover_i%2Ceditions.series");
        parameters.Add("limit=5");
        return "search.json?" + string.Join('&', parameters);
    }

    private static EditionMetadataCandidate? TryCreateEditionCandidate(OpenLibraryEditionWork work)
    {
        OpenLibraryEdition? edition = work.Editions?.Docs?.FirstOrDefault();
        if (!IsWorkId(work.Key) || !IsEditionId(edition?.Key)
            || string.IsNullOrWhiteSpace(edition?.Title) && string.IsNullOrWhiteSpace(work.Title)
            || work.AuthorName is not { Length: > 0 })
            return null;
        try
        {
            EditionMetadataIdentifier[] identifiers = (edition!.Isbn ?? [])
                .Select(TryCreateIsbn).Where(value => value is not null).Select(value => value!)
                .Distinct().Take(16).ToArray();
            int? coverId = edition.CoverId is > 0 ? edition.CoverId : null;
            return new(
                work.Key!,
                edition.Key!,
                string.IsNullOrWhiteSpace(edition.Title) ? work.Title! : edition.Title!,
                work.AuthorName!,
                identifiers,
                edition.Publisher?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                ParsePublicationDate(edition.PublishDate?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))),
                edition.Language,
                edition.Series?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                cover: coverId is null ? null : new(
                    coverId.Value.ToString(CultureInfo.InvariantCulture),
                    $"https://covers.openlibrary.org/b/id/{coverId.Value.ToString(CultureInfo.InvariantCulture)}-L.jpg"));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static EditionMetadataIdentifier? TryCreateIsbn(string value)
    {
        try { return new("isbn", value); }
        catch (ArgumentException) { return null; }
    }

    private static EditionPublicationDate? ParsePublicationDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        if (DateTime.TryParseExact(normalized, "yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime year))
            return new(year.Year);
        string[] monthFormats = ["yyyy-MM", "MMMM yyyy", "MMM yyyy"];
        if (DateTime.TryParseExact(normalized, monthFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out DateTime month))
            return new(month.Year, month.Month);
        string[] dayFormats = ["yyyy-MM-dd", "MMMM d, yyyy", "MMM d, yyyy", "d MMMM yyyy", "d MMM yyyy"];
        return DateTime.TryParseExact(normalized, dayFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out DateTime day)
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
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Provider response is too large.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool IsWorkId(string? value) => value is { Length: >= 12 and <= 160 }
        && value.StartsWith("/works/OL", StringComparison.Ordinal)
        && value.EndsWith('W')
        && value[9..^1].All(char.IsAsciiDigit);

    private static bool IsEditionId(string? value) => value is { Length: >= 12 and <= 160 }
        && value.StartsWith("/books/OL", StringComparison.Ordinal)
        && value.EndsWith('M')
        && value[9..^1].All(char.IsAsciiDigit);

    private sealed record OpenLibraryDocument(
        [property: JsonPropertyName("docs")] OpenLibraryWork[]? Docs);

    private sealed record OpenLibraryWork(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("author_name")] string[]? AuthorName,
        [property: JsonPropertyName("language")] string[]? Language,
        [property: JsonPropertyName("edition_count")] int EditionCount);

    private sealed record OpenLibraryEditionSearchDocument(
        [property: JsonPropertyName("docs")] OpenLibraryEditionWork[]? Docs);

    private sealed record OpenLibraryEditionWork(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("author_name")] string[]? AuthorName,
        [property: JsonPropertyName("editions")] OpenLibraryEditions? Editions);

    private sealed record OpenLibraryEditions(
        [property: JsonPropertyName("docs")] OpenLibraryEdition[]? Docs);

    private sealed record OpenLibraryEdition(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("language")] string[]? Language,
        [property: JsonPropertyName("isbn")] string[]? Isbn,
        [property: JsonPropertyName("publisher")] string[]? Publisher,
        [property: JsonPropertyName("publish_date")] string[]? PublishDate,
        [property: JsonPropertyName("cover_i")] int? CoverId,
        [property: JsonPropertyName("series")] string[]? Series);
}
