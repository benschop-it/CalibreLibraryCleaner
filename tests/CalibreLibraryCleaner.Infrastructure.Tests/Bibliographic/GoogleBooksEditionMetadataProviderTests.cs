using System.Net;
using System.Text;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class GoogleBooksEditionMetadataProviderTests
{
    private const string ApiKey = "AIzaFakePrivateKey_1234567890";

    [Fact]
    public async Task IsbnSearchSendsDisclosedFieldsAndParsesBoundedVolumeMetadata()
    {
        RecordingHandler handler = new(ApiKey, _ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"totalItems":1,"items":[{"id":"zyTCAlFPjgYC","volumeInfo":{
                  "title":"The Google Story","subtitle":"Inside the Company",
                  "authors":["David A. Vise","Mark Malseed"],
                  "publisher":"Random House","publishedDate":"2005-11-15",
                  "industryIdentifiers":[
                    {"type":"ISBN_10","identifier":"055380457X"},
                    {"type":"ISBN_13","identifier":"9780553804577"},
                    {"type":"ISSN","identifier":"ignored"}],
                  "language":"en",
                  "imageLinks":{"thumbnail":"https://example.invalid/not-requested.jpg"},
                  "description":"not requested or retained"
                }}]}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        IGoogleBooksApiKeyStore store = KeyStore(ApiKey);
        using HttpClient client = Client(handler);
        using GoogleBooksEditionMetadataProvider provider = new(client, store, new());

        EditionMetadataSearchResult result = await provider.SearchAsync(
            IsbnQuery(provider.Identity), CancellationToken.None);

        result.Status.Should().Be(EditionMetadataSearchStatus.Success);
        EditionMetadataCandidate candidate = result.Candidates.Should().ContainSingle().Which;
        candidate.WorkId.Should().Be("google-books-volume:zyTCAlFPjgYC");
        candidate.EditionId.Should().Be(candidate.WorkId);
        candidate.Title.Should().Be("The Google Story: Inside the Company");
        candidate.Authors.Should().Equal("David A. Vise", "Mark Malseed");
        candidate.Publisher.Should().Be("Random House");
        candidate.PublicationDate.Should().Be(new EditionPublicationDate(2005, 11, 15));
        candidate.Identifiers.Should().HaveCount(2);
        candidate.Languages.Should().Equal("en");
        candidate.Cover.Should().BeNull();
        handler.KeyMatched.Should().BeTrue();
        handler.AuthorizationPresent.Should().BeFalse();
        string decodedQuery = Uri.UnescapeDataString(handler.RequestUri!.Query);
        decodedQuery.Should().Contain("q=isbn:9780553804577");
        decodedQuery.Should().Contain("maxResults=5");
        decodedQuery.Should().Contain("printType=books");
        decodedQuery.Should().Contain("projection=lite");
        decodedQuery.Should().Contain("fields=items(id,volumeInfo(title,subtitle,authors,publisher,publishedDate,industryIdentifiers,language)),totalItems");
        decodedQuery.Should().NotContain("description");
        decodedQuery.Should().NotContain("imageLinks");
    }

    [Fact]
    public async Task TitleAuthorSearchDisclosesOnlyTitleAuthorsAndOptionalLanguage()
    {
        RecordingHandler handler = new(ApiKey, _ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"totalItems\":0}", Encoding.UTF8, "application/json"),
        });
        using HttpClient client = Client(handler);
        using GoogleBooksEditionMetadataProvider provider = new(client, KeyStore(ApiKey), new());
        EditionMetadataLookupQuery query = new(
            new(1),
            provider.Identity,
            EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors
                | EditionMetadataQueryFields.Language,
            null,
            "Private Title",
            ["Private Author"],
            "en");

        EditionMetadataSearchResult result = await provider.SearchAsync(query, CancellationToken.None);

        result.Status.Should().Be(EditionMetadataSearchStatus.NotFound);
        string decodedQuery = Uri.UnescapeDataString(handler.RequestUri!.Query);
        decodedQuery.Should().Contain("intitle:\"Private Title\"");
        decodedQuery.Should().Contain("inauthor:\"Private Author\"");
        decodedQuery.Should().Contain("langRestrict=en");
        decodedQuery.Contains("path", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task MissingKeyDisablesOnlyGoogleProviderWithoutSendingRequest()
    {
        RecordingHandler handler = new(ApiKey, _ => throw new InvalidOperationException("No request expected."));
        using HttpClient client = Client(handler);
        using GoogleBooksEditionMetadataProvider provider = new(client, KeyStore(null), new());

        EditionMetadataSearchResult result = await provider.SearchAsync(
            IsbnQuery(provider.Identity), CancellationToken.None);

        result.Status.Should().Be(EditionMetadataSearchStatus.Unavailable);
        result.ProblemCode.Should().Be("GOOGLE_BOOKS.API_KEY_NOT_CONFIGURED");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task HttpAndOversizeFailuresAreBoundedAndDoNotExposeKey()
    {
        RecordingHandler unavailableHandler = new(ApiKey, _ => new(HttpStatusCode.TooManyRequests));
        using HttpClient unavailableClient = Client(unavailableHandler);
        using GoogleBooksEditionMetadataProvider unavailable = new(
            unavailableClient, KeyStore(ApiKey), new());
        RecordingHandler oversizedHandler = new(ApiKey, _ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[16 * 1024 + 1]),
        });
        using HttpClient oversizedClient = Client(oversizedHandler);
        using GoogleBooksEditionMetadataProvider oversized = new(
            oversizedClient, KeyStore(ApiKey), new(maximumResponseBytes: 16 * 1024));

        EditionMetadataSearchResult httpResult = await unavailable.SearchAsync(
            IsbnQuery(unavailable.Identity), CancellationToken.None);
        EditionMetadataSearchResult oversizedResult = await oversized.SearchAsync(
            IsbnQuery(oversized.Identity), CancellationToken.None);

        httpResult.ProblemCode.Should().Be("GOOGLE_BOOKS.HTTP_429");
        oversizedResult.ProblemCode.Should().Be("GOOGLE_BOOKS.RESPONSE_TOO_LARGE");
        httpResult.ProblemCode.Should().NotContain(ApiKey);
        oversizedResult.ProblemCode.Should().NotContain(ApiKey);
    }

    private static EditionMetadataLookupQuery IsbnQuery(EditionMetadataProviderIdentity provider) => new(
        new(1),
        provider,
        EditionMetadataQueryFields.Identifier,
        "ISBN:9780553804577",
        null,
        null,
        null);

    private static IGoogleBooksApiKeyStore KeyStore(string? value)
    {
        IGoogleBooksApiKeyStore store = A.Fake<IGoogleBooksApiKeyStore>();
        A.CallTo(() => store.ReadAsync(A<CancellationToken>._)).Returns(value);
        return store;
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new("https://www.googleapis.com/books/v1/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private sealed class RecordingHandler(
        string expectedKey,
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public int RequestCount { get; private set; }
        public bool KeyMatched { get; private set; }
        public bool AuthorizationPresent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestUri = request.RequestUri;
            AuthorizationPresent = request.Headers.Authorization is not null;
            string encodedKey = "key=" + Uri.EscapeDataString(expectedKey);
            KeyMatched = request.RequestUri?.Query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(value => value.TrimStart('?') == encodedKey) == true;
            return Task.FromResult(response(request));
        }
    }
}
