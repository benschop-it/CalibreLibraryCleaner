using System.Net;
using System.Net.Http.Headers;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Metadata;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Metadata;

public sealed class BoundedEditionCoverStagerTests
{
    [Fact]
    public async Task TrustedJpegStagesWithFingerprintAndSessionCleanup()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => Response(Jpeg(320, 480)));
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        IEditionCoverStagingSession session = result.Session!;
        StagedEditionCover staged = session.Covers["subject"];
        staged.FileName.Should().Be("cover-00000.jpg");
        staged.Fingerprint.SizeInBytes.Should().Be(Jpeg(320, 480).Length);
        File.Exists(Path.Combine(session.StagingRoot, staged.FileName)).Should().BeTrue();
        handler.RequestCount.Should().Be(1);

        string root = session.StagingRoot;
        await session.DisposeAsync();
        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public async Task UntrustedEndpointFailsWithoutHttpOrBatchResidue()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => Response(Jpeg(1, 1)));
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://example.com/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("METADATA_COVER.STAGING_FAILED");
        handler.RequestCount.Should().Be(0);
        Directory.EnumerateDirectories(temporary.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task OversizedDimensionsFailAndDeletePartialBatch()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => Response(Jpeg(10_001, 1)));
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        handler.RequestCount.Should().Be(1);
        Directory.EnumerateDirectories(temporary.Path).Should().BeEmpty();
    }

    private static BoundedEditionCoverStager Stager(string root, HttpMessageHandler handler) => new(
        new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
        new() { StorageRoot = root });

    private static EditionCoverReference Cover(string url) => new("123", url);

    private static HttpResponseMessage Response(byte[] content)
    {
        ByteArrayContent body = new(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new(HttpStatusCode.OK) { Content = body };
    }

    private static byte[] Jpeg(int width, int height) =>
    [
        0xff, 0xd8,
        0xff, 0xc0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x03,
        0x01, 0x11, 0x00,
        0x02, 0x11, 0x00,
        0x03, 0x11, 0x00,
        0xff, 0xd9,
    ];

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }
}
