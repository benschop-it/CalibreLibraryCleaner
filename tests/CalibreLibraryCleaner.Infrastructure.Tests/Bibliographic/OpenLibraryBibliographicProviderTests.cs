using System.Diagnostics;
using System.Net;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class OpenLibraryBibliographicProviderTests
{
    private static readonly BibliographicProviderIdentity Provider = new(
        "open-library", "open-library-search-1.0.0");

    [Fact]
    public async Task SearchSendsOnlyDisclosedFieldsAndParsesBoundedWorkFacts()
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"docs":[{"key":"/works/OL138052W","title":"Alice's Adventures in Wonderland","author_name":["Lewis Carroll"],"language":["eng"],"edition_count":480}]}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using HttpClient client = Client(handler);
        using OpenLibraryBibliographicProvider provider = new(
            client, new(minimumRequestInterval: TimeSpan.Zero));

        BibliographicSearchResult result = await provider.SearchAsync(
            Query("Alice in Wonderland Illustrated", "L. Carroll"), CancellationToken.None);

        result.Status.Should().Be(BibliographicSearchStatus.Success);
        result.Candidates.Should().ContainSingle().Which.WorkId.Should().Be("/works/OL138052W");
        handler.RequestUri!.AbsolutePath.Should().Be("/search.json");
        string query = handler.RequestUri.Query;
        query.Should().Contain("title=Alice%20in%20Wonderland%20Illustrated");
        query.Should().Contain("author=L.%20Carroll");
        query.Should().Contain("lang=en");
        query.Should().Contain("fields=key%2Ctitle%2Cauthor_name%2Clanguage%2Cedition_count");
        query.Should().Contain("limit=5");
        query.Contains("path", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        handler.UserAgent.Should().Contain("CalibreLibraryCleaner/1.0");
    }

    [Fact]
    public async Task HttpAndOversizeFailuresBecomeUnavailableWithoutPayloadDetails()
    {
        RecordingHandler unavailableHandler = new(_ => new(HttpStatusCode.TooManyRequests));
        using HttpClient unavailableClient = Client(unavailableHandler);
        using OpenLibraryBibliographicProvider unavailable = new(
            unavailableClient, new(minimumRequestInterval: TimeSpan.Zero));
        RecordingHandler oversizedHandler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[16 * 1024 + 1]),
        });
        using HttpClient oversizedClient = Client(oversizedHandler);
        using OpenLibraryBibliographicProvider oversized = new(
            oversizedClient,
            new(minimumRequestInterval: TimeSpan.Zero, maximumResponseBytes: 16 * 1024));

        BibliographicSearchResult httpResult = await unavailable.SearchAsync(
            Query("Private Title", "Private Author"), CancellationToken.None);
        BibliographicSearchResult oversizedResult = await oversized.SearchAsync(
            Query("Private Title", "Private Author"), CancellationToken.None);

        httpResult.Status.Should().Be(BibliographicSearchStatus.Unavailable);
        httpResult.ProblemCode.Should().Be("OPEN_LIBRARY.HTTP_429");
        oversizedResult.Status.Should().Be(BibliographicSearchStatus.Unavailable);
        oversizedResult.ProblemCode.Should().Be("OPEN_LIBRARY.RESPONSE_TOO_LARGE");
        httpResult.ProblemCode.Should().NotContain("Private");
    }

    [Fact]
    public async Task HangingRequestHonorsProviderTimeout()
    {
        using HttpClient client = Client(new HangingHandler());
        using OpenLibraryBibliographicProvider provider = new(
            client,
            new(requestTimeout: TimeSpan.FromSeconds(1), minimumRequestInterval: TimeSpan.Zero));
        Stopwatch stopwatch = Stopwatch.StartNew();

        BibliographicSearchResult result = await provider.SearchAsync(
            Query("Private Title", "Private Author"), CancellationToken.None);

        result.Status.Should().Be(BibliographicSearchStatus.Unavailable);
        result.ProblemCode.Should().Be("OPEN_LIBRARY.TIMEOUT");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void OptionsStringDoesNotExposeConfiguredContact()
    {
        OpenLibraryOptions options = new(contact: "private@example.org");

        options.ToString().Should().NotContain("private@example.org");
    }

    private static BibliographicLookupQuery Query(string title, string author) => new(
        new(1),
        Provider,
        BibliographicQueryFields.Title | BibliographicQueryFields.Authors | BibliographicQueryFields.Language,
        null,
        title,
        [author],
        "en");

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new("https://openlibrary.org/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) :
        HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            UserAgent = string.Join(' ', request.Headers.GetValues("User-Agent"));
            return Task.FromResult(response(request));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timeout should cancel the request.");
        }
    }
}
