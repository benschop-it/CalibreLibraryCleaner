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
    OpenLibraryOptions options) : IBibliographicProvider, IEditionMetadataProvider,
    IBudgetedEditionMetadataProvider, IDisposable
{
    private const string ProviderVersion = "open-library-search-1.0.0";
    private const string EditionProviderVersion = "open-library-edition-search/1.2.0";
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
        CancellationToken cancellationToken) => (await SearchAsync(
            query, 1, cancellationToken).ConfigureAwait(false)).Result;

    Task<EditionMetadataSearchExecution> IBudgetedEditionMetadataProvider.SearchAsync(
        EditionMetadataLookupQuery query,
        int maximumProviderRequests,
        CancellationToken cancellationToken) => SearchAsync(
            query, maximumProviderRequests, cancellationToken);

    private async Task<EditionMetadataSearchExecution> SearchAsync(
        EditionMetadataLookupQuery query,
        int maximumProviderRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumProviderRequests, 1);
        EditionMetadataProviderIdentity editionIdentity = ((IEditionMetadataProvider)this).Identity;
        if (query.Provider != editionIdentity)
            throw new ArgumentException("The query targets another edition metadata provider.", nameof(query));
        if (!options.Enabled)
            return new(Unavailable("OPEN_LIBRARY.DISABLED"), 0);

        int requestCount = 0;
        await WaitForRateSlotAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, BuildRelativeUri(query));
            string userAgent = "CalibreLibraryCleaner/1.0 (+https://github.com/benschop-it/CalibreLibraryCleaner)";
            if (options.Contact is not null) userAgent += $" ({options.Contact})";
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            requestCount++;
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(new(editionIdentity, EditionMetadataSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "OPEN_LIBRARY.NOT_FOUND"), requestCount);
            if (!response.IsSuccessStatusCode)
                return new(Unavailable($"OPEN_LIBRARY.HTTP_{(int)response.StatusCode}"), requestCount);
            if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
                return new(Unavailable("OPEN_LIBRARY.RESPONSE_TOO_LARGE"), requestCount);

            byte[] bytes = await ReadBoundedAsync(
                response.Content, options.MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            OpenLibraryEditionSearchDocument? document = JsonSerializer.Deserialize<OpenLibraryEditionSearchDocument>(
                bytes, JsonOptions);
            OpenLibraryEditionWork[] works = (document?.Docs ?? []).Take(5).ToArray();
            EditionMetadataCandidate[] candidates = works
                .SelectMany(CreateCandidatesForWork)
                .GroupBy(value => value.Candidate.EditionId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(value => CandidateRichness(value.Candidate))
                .ThenBy(value => value.WorkIndex)
                .ThenBy(value => value.EditionIndex)
                .Select(value => value.Candidate)
                .Take(5)
                .ToArray();
            OpenLibraryEditionWork? fallbackWork = candidates.Length == 0 && maximumProviderRequests >= 2
                ? works.FirstOrDefault(value => IsCompatibleFallbackWork(query, value))
                : null;
            if (fallbackWork is not null)
            {
                await WaitForRateSlotAsync(cancellationToken).ConfigureAwait(false);
                using HttpRequestMessage editionsRequest = new(
                    HttpMethod.Get, BuildWorkEditionsRelativeUri(fallbackWork.Key!));
                editionsRequest.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                using CancellationTokenSource editionsTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                editionsTimeout.CancelAfter(options.RequestTimeout);
                requestCount++;
                using HttpResponseMessage editionsResponse = await httpClient.SendAsync(
                    editionsRequest, HttpCompletionOption.ResponseHeadersRead, editionsTimeout.Token)
                    .ConfigureAwait(false);
                if (editionsResponse.IsSuccessStatusCode)
                {
                    if (editionsResponse.Content.Headers.ContentLength > options.MaximumResponseBytes)
                        return new(Unavailable("OPEN_LIBRARY.RESPONSE_TOO_LARGE"), requestCount);
                    byte[] editionBytes = await ReadBoundedAsync(
                        editionsResponse.Content, options.MaximumResponseBytes, editionsTimeout.Token)
                        .ConfigureAwait(false);
                    OpenLibraryWorkEditionsDocument? editions =
                        JsonSerializer.Deserialize<OpenLibraryWorkEditionsDocument>(editionBytes, JsonOptions);
                    candidates = (editions?.Entries ?? [])
                        .Take(5)
                        .Select(value => TryCreateWorkEditionCandidate(fallbackWork, value))
                        .Where(value => value is not null)
                        .Select(value => value!)
                        .OrderByDescending(CandidateRichness)
                        .ThenBy(value => value.EditionId, StringComparer.Ordinal)
                        .Take(5)
                        .ToArray();
                }
                else if (editionsResponse.StatusCode != HttpStatusCode.NotFound)
                {
                    return new(Unavailable($"OPEN_LIBRARY.HTTP_{(int)editionsResponse.StatusCode}"), requestCount);
                }
            }
            EditionMetadataSearchResult result = candidates.Length == 0
                ? new(editionIdentity, EditionMetadataSearchStatus.NotFound, DateTimeOffset.UtcNow,
                    problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS")
                : new(editionIdentity, EditionMetadataSearchStatus.Success, DateTimeOffset.UtcNow, candidates);
            return new(result, requestCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(Unavailable("OPEN_LIBRARY.TIMEOUT"), requestCount);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException or ArgumentException or OverflowException)
        {
            return new(Unavailable("OPEN_LIBRARY.UNAVAILABLE"), requestCount);
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
            if (query.Authors.Count > 0)
                parameters.Add("author=" + Uri.EscapeDataString(string.Join("; ", query.Authors)));
            if (query.Language is not null) parameters.Add("lang=" + Uri.EscapeDataString(query.Language));
        }
        parameters.Add("fields=key%2Ctitle%2Cauthor_name%2Ceditions%2Ceditions.key%2Ceditions.title%2Ceditions.language%2Ceditions.isbn%2Ceditions.publisher%2Ceditions.publish_date%2Ceditions.cover_i%2Ceditions.series");
        parameters.Add("limit=5");
        return "search.json?" + string.Join('&', parameters);
    }

    private static string BuildWorkEditionsRelativeUri(string workId) =>
        workId.TrimStart('/') + "/editions.json?limit=5";

    private static EditionMetadataCandidate? TryCreateEditionCandidate(
        OpenLibraryEditionWork work,
        OpenLibraryEdition edition)
    {
        if (!IsWorkId(work.Key) || !IsEditionId(edition.Key)
            || string.IsNullOrWhiteSpace(edition.Title) && string.IsNullOrWhiteSpace(work.Title)
            || work.AuthorName is not { Length: > 0 })
            return null;
        try
        {
            EditionMetadataIdentifier[] identifiers = (edition.Isbn ?? [])
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

    private static int CandidateRichness(EditionMetadataCandidate candidate) =>
        candidate.Identifiers.Count
        + (candidate.Publisher is null ? 0 : 1)
        + (candidate.PublicationDate is null ? 0 : 1)
        + candidate.Languages.Count
        + (candidate.Series is null ? 0 : 1)
        + (candidate.Cover is null ? 0 : 1);

    private static IEnumerable<IndexedEditionCandidate> CreateCandidatesForWork(
        OpenLibraryEditionWork work,
        int workIndex)
    {
        IndexedEditionCandidate[] editions = (work.Editions?.Docs ?? [])
            .Take(5)
            .Select((edition, editionIndex) => new
            {
                Candidate = TryCreateEditionCandidate(work, edition),
                EditionIndex = editionIndex,
            })
            .Where(value => value.Candidate is not null)
            .Select(value => new IndexedEditionCandidate(
                value.Candidate!, workIndex, value.EditionIndex))
            .ToArray();
        return editions;
    }

    private static bool IsUsableWork(OpenLibraryEditionWork work) =>
        IsWorkId(work.Key) && !string.IsNullOrWhiteSpace(work.Title)
        && work.AuthorName is { Length: > 0 };

    private static bool IsCompatibleFallbackWork(
        EditionMetadataLookupQuery query,
        OpenLibraryEditionWork work)
    {
        if (!IsUsableWork(work)) return false;
        if (query.Identifier is not null) return true;
        string[] queryAuthors = CandidateMetadataNormalizer.AuthorKeys(
            query.Authors, normalizePseudonymAnnotations: false);
        string[] workAuthors = CandidateMetadataNormalizer.AuthorKeys(
            work.AuthorName!, normalizePseudonymAnnotations: false);
        if (query.Authors.Count > 0
            && !CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(queryAuthors, workAuthors)) return false;
        string[] queryTitleKeys = CandidateMetadataNormalizer.TitleKeys(query.Title!);
        string[] workTitleKeys = CandidateMetadataNormalizer.TitleKeys(work.Title!);
        return queryTitleKeys.Intersect(workTitleKeys, StringComparer.Ordinal).Any()
            || CandidateMetadataNormalizer.TitleSimilarityPermille(
                CandidateMetadataNormalizer.TitleTokens(query.Title!),
                CandidateMetadataNormalizer.TitleTokens(work.Title!)) >= 500;
    }

    private static EditionMetadataCandidate? TryCreateWorkEditionCandidate(
        OpenLibraryEditionWork work,
        OpenLibraryWorkEdition edition)
    {
        if (!IsUsableWork(work) || !IsEditionId(edition.Key)
            || string.IsNullOrWhiteSpace(edition.Title) && string.IsNullOrWhiteSpace(work.Title))
            return null;
        try
        {
            EditionMetadataIdentifier[] identifiers = (edition.Isbn10 ?? [])
                .Concat(edition.Isbn13 ?? [])
                .Select(TryCreateIsbn).Where(value => value is not null).Select(value => value!)
                .Distinct().Take(16).ToArray();
            string[] languages = (edition.Languages ?? [])
                .Select(value => value.Key)
                .Where(value => value is not null && value.StartsWith("/languages/", StringComparison.Ordinal))
                .Select(value => value![11..])
                .ToArray();
            int? coverId = edition.Covers?.FirstOrDefault(value => value > 0);
            return new(
                work.Key!,
                edition.Key!,
                string.IsNullOrWhiteSpace(edition.Title) ? work.Title! : edition.Title!,
                work.AuthorName!,
                identifiers,
                edition.Publishers?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                ParsePublicationDate(edition.PublishDate),
                languages,
                edition.Series?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                cover: coverId is null or 0 ? null : new(
                    coverId.Value.ToString(CultureInfo.InvariantCulture),
                    $"https://covers.openlibrary.org/b/id/{coverId.Value.ToString(CultureInfo.InvariantCulture)}-L.jpg"));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record IndexedEditionCandidate(
        EditionMetadataCandidate Candidate,
        int WorkIndex,
        int EditionIndex);

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
        string[] monthFormats = ["yyyy-MM", "MMMM yyyy", "MMM yyyy", "yyyy MMMM", "yyyy MMM"];
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

    private sealed record OpenLibraryWorkEditionsDocument(
        [property: JsonPropertyName("entries")] OpenLibraryWorkEdition[]? Entries);

    private sealed record OpenLibraryWorkEdition(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("isbn_10")] string[]? Isbn10,
        [property: JsonPropertyName("isbn_13")] string[]? Isbn13,
        [property: JsonPropertyName("publishers")] string[]? Publishers,
        [property: JsonPropertyName("publish_date")] string? PublishDate,
        [property: JsonPropertyName("languages")] OpenLibraryLanguageReference[]? Languages,
        [property: JsonPropertyName("covers")] int[]? Covers,
        [property: JsonPropertyName("series")] string[]? Series);

    private sealed record OpenLibraryLanguageReference(
        [property: JsonPropertyName("key")] string? Key);
}
