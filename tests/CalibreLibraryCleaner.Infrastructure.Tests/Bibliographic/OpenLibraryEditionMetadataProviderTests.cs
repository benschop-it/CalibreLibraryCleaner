using System.Net;
using System.Text;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class OpenLibraryEditionMetadataProviderTests
{
    [Fact]
    public async Task SearchRequestsAndParsesOneCoherentNestedEdition()
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"docs":[{
                  "key":"/works/OL66554W",
                  "title":"Pride and Prejudice",
                  "author_name":["Jane Austen"],
                  "isbn":["9780000000002"],
                  "publisher":["Wrong Aggregated Publisher"],
                  "editions":{"docs":[{
                    "key":"/books/OL7353617M",
                    "title":"Pride and Prejudice",
                    "language":["eng"],
                    "isbn":["9780141439518","invalid"],
                    "publisher":["Penguin Classics"],
                    "publish_date":["January 29, 2003"],
                    "cover_i":8225261,
                    "series":["Penguin Classics"]
                  }]}
                }]}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using HttpClient client = new(handler)
        {
            BaseAddress = new("https://openlibrary.org/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using OpenLibraryBibliographicProvider provider = new(
            client, new(minimumRequestInterval: TimeSpan.Zero));
        IEditionMetadataProvider editionProvider = provider;
        EditionMetadataLookupQuery query = new(
            new(1),
            editionProvider.Identity,
            EditionMetadataQueryFields.Identifier,
            "ISBN:9780141439518",
            null,
            null,
            null);

        EditionMetadataSearchResult result = await editionProvider.SearchAsync(
            query, CancellationToken.None);

        result.Status.Should().Be(EditionMetadataSearchStatus.Success);
        EditionMetadataCandidate candidate = result.Candidates.Should().ContainSingle().Which;
        candidate.WorkId.Should().Be("/works/OL66554W");
        candidate.EditionId.Should().Be("/books/OL7353617M");
        candidate.Identifiers.Should().ContainSingle(value => value.Value == "9780141439518");
        candidate.Publisher.Should().Be("Penguin Classics");
        candidate.PublicationDate.Should().Be(new EditionPublicationDate(2003, 1, 29));
        candidate.Languages.Should().Equal("en");
        candidate.Series.Should().Be("Penguin Classics");
        candidate.Cover!.SourceId.Should().Be("8225261");
        candidate.Cover.Url.Should().Be("https://covers.openlibrary.org/b/id/8225261-L.jpg");
        handler.RequestUri!.AbsolutePath.Should().Be("/search.json");
        handler.RequestUri.Query.Should().Contain("isbn=9780141439518");
        handler.RequestUri.Query.Should().Contain("editions.key");
        handler.RequestUri.Query.Should().Contain("editions.isbn");
        handler.RequestUri.Query.Should().Contain("editions.cover_i");
        handler.RequestUri.Query.Should().NotContain("Wrong%20Aggregated%20Publisher");
        handler.RequestUri.Query.Contains("path", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task MissingNestedEditionBecomesNotFoundWithoutPayloadDetails()
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"docs":[{"key":"/works/OL66554W","title":"Private Payload","author_name":["Private Author"]}]}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using HttpClient client = new(handler)
        {
            BaseAddress = new("https://openlibrary.org/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using OpenLibraryBibliographicProvider provider = new(
            client, new(minimumRequestInterval: TimeSpan.Zero));
        IEditionMetadataProvider editionProvider = provider;

        EditionMetadataSearchResult result = await editionProvider.SearchAsync(
            Query(editionProvider.Identity), CancellationToken.None);

        result.Status.Should().Be(EditionMetadataSearchStatus.NotFound);
        result.ProblemCode.Should().Be("OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS");
        result.ProblemCode.Should().NotContain("Private");
    }

    [Fact]
    public async Task HttpAndOversizeFailuresRemainUnavailableWithoutPayloadDetails()
    {
        RecordingHandler unavailableHandler = new(_ => new(HttpStatusCode.TooManyRequests));
        using HttpClient unavailableClient = new(unavailableHandler)
        {
            BaseAddress = new("https://openlibrary.org/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using OpenLibraryBibliographicProvider unavailable = new(
            unavailableClient, new(minimumRequestInterval: TimeSpan.Zero));
        IEditionMetadataProvider unavailableProvider = unavailable;
        RecordingHandler oversizedHandler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[16 * 1024 + 1]),
        });
        using HttpClient oversizedClient = new(oversizedHandler)
        {
            BaseAddress = new("https://openlibrary.org/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using OpenLibraryBibliographicProvider oversized = new(
            oversizedClient,
            new(minimumRequestInterval: TimeSpan.Zero, maximumResponseBytes: 16 * 1024));
        IEditionMetadataProvider oversizedProvider = oversized;

        EditionMetadataSearchResult httpResult = await unavailableProvider.SearchAsync(
            Query(unavailableProvider.Identity), CancellationToken.None);
        EditionMetadataSearchResult oversizedResult = await oversizedProvider.SearchAsync(
            Query(oversizedProvider.Identity), CancellationToken.None);

        httpResult.Status.Should().Be(EditionMetadataSearchStatus.Unavailable);
        httpResult.ProblemCode.Should().Be("OPEN_LIBRARY.HTTP_429");
        oversizedResult.Status.Should().Be(EditionMetadataSearchStatus.Unavailable);
        oversizedResult.ProblemCode.Should().Be("OPEN_LIBRARY.RESPONSE_TOO_LARGE");
        httpResult.ProblemCode.Should().NotContain("Private");
    }

    private static EditionMetadataLookupQuery Query(EditionMetadataProviderIdentity provider) => new(
        new(1),
        provider,
        EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors
            | EditionMetadataQueryFields.Language,
        null,
        "Pride and Prejudice",
        ["Jane Austen"],
        "en");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) :
        HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            return Task.FromResult(response(request));
        }
    }
}
