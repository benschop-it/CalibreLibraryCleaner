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
        result.CompletedCount.Should().Be(1);
        result.TotalCount.Should().Be(1);
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
    public async Task ValidatedCoverCacheSurvivesSessionDisposalAndAvoidsSecondRequest()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler firstHandler = new(_ => Response(Jpeg(320, 480)));
        using (BoundedEditionCoverStager first = Stager(temporary.Path, firstHandler))
        {
            EditionCoverStagingResult initial = await first.StageAsync(
                [new("first-subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
                null,
                CancellationToken.None);
            initial.IsSuccess.Should().BeTrue(initial.FailureCode);
            await initial.Session!.DisposeAsync();
        }

        RecordingHandler secondHandler = new(_ => new(HttpStatusCode.ServiceUnavailable));
        using BoundedEditionCoverStager second = Stager(temporary.Path, secondHandler);
        List<EditionCoverStagingProgress> progress = [];
        EditionCoverStagingResult resumed = await second.StageAsync(
            [new("second-subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            new InlineProgress<EditionCoverStagingProgress>(progress.Add),
            CancellationToken.None);

        resumed.IsSuccess.Should().BeTrue(resumed.FailureCode);
        resumed.Session!.Covers.Should().ContainKey("second-subject");
        firstHandler.RequestCount.Should().Be(1);
        secondHandler.RequestCount.Should().Be(0);
        progress.Should().ContainSingle().Which.Should().Be(
            new EditionCoverStagingProgress(1, 1, 1, 0, 0));
        Directory.EnumerateFiles(Path.Combine(temporary.Path, "cache"), "*.jpg").Should().ContainSingle();
        Directory.EnumerateFiles(Path.Combine(temporary.Path, "cache"), "*.json").Should().ContainSingle();
        await resumed.Session.DisposeAsync();
    }

    [Fact]
    public async Task CorruptCoverCacheEntryBecomesMissAndIsReplaced()
    {
        using TemporaryDirectory temporary = new();
        using (BoundedEditionCoverStager first = Stager(
                   temporary.Path, new RecordingHandler(_ => Response(Jpeg(320, 480)))))
        {
            EditionCoverStagingResult initial = await first.StageAsync(
                [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
                null,
                CancellationToken.None);
            await initial.Session!.DisposeAsync();
        }
        string cached = Directory.EnumerateFiles(Path.Combine(temporary.Path, "cache"), "*.jpg").Single();
        await File.WriteAllBytesAsync(cached, [0x00, 0x01, 0x02]);
        RecordingHandler replacementHandler = new(_ => Response(Jpeg(640, 960)));
        using BoundedEditionCoverStager replacement = Stager(temporary.Path, replacementHandler);

        EditionCoverStagingResult result = await replacement.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        replacementHandler.RequestCount.Should().Be(1);
        result.Session!.Covers["subject"].Fingerprint.SizeInBytes.Should().Be(Jpeg(640, 960).Length);
        await result.Session.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableCoverCacheDegradesToDownload()
    {
        using TemporaryDirectory temporary = new();
        string cacheFile = Path.Combine(temporary.Path, "cache-file");
        await File.WriteAllTextAsync(cacheFile, "not a directory");
        RecordingHandler handler = new(_ => Response(Jpeg(320, 480)));
        using BoundedEditionCoverStager stager = new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new()
            {
                StorageRoot = StagingRoot(temporary.Path),
                CacheRoot = cacheFile,
                RetryDelay = TimeSpan.Zero,
            });

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        handler.RequestCount.Should().Be(1);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task CoverCacheEnforcesConfiguredEntryBound()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => Response(Jpeg(320, 480)));
        using BoundedEditionCoverStager stager = new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new()
            {
                StorageRoot = StagingRoot(temporary.Path),
                CacheRoot = Path.Combine(temporary.Path, "cache"),
                MaximumCacheEntries = 1,
                RetryDelay = TimeSpan.Zero,
            });

        EditionCoverStagingResult result = await stager.StageAsync(
            [
                new("first", new("123", "https://covers.openlibrary.org/b/id/123-L.jpg")),
                new("second", new("456", "https://covers.openlibrary.org/b/id/456-L.jpg")),
            ],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        handler.RequestCount.Should().Be(2);
        Directory.EnumerateFiles(Path.Combine(temporary.Path, "cache"), "*.jpg").Should().ContainSingle();
        Directory.EnumerateFiles(Path.Combine(temporary.Path, "cache"), "*.json").Should().ContainSingle();
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task TrustedOpenLibraryRedirectChainStagesJpeg()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(request => request.RequestUri!.Host switch
        {
            "covers.openlibrary.org" => Redirect(
                "https://archive.org/download/l_covers_0012/l_covers_0012_34.zip/00456-L.jpg"),
            "archive.org" => Redirect(
                "https://ia800404.us.archive.org/view_archive.php?archive=olcovers.zip&file=00456-L.jpg"),
            "ia800404.us.archive.org" => Response(Jpeg(320, 480)),
            _ => new(HttpStatusCode.NotFound),
        });
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        handler.RequestCount.Should().Be(3);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task UntrustedRedirectFailsWithoutFollowingTarget()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => Redirect("https://example.com/123-L.jpg"));
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        handler.RequestCount.Should().Be(1);
        Directory.EnumerateDirectories(StagingRoot(temporary.Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task PersistentAdditionalRedirectRetriesWithoutFollowingTarget()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(request => request.RequestUri!.Host switch
        {
            "covers.openlibrary.org" => Redirect(
                "https://archive.org/download/olcovers12/olcovers12-L.zip/123-L.jpg"),
            "archive.org" => Redirect(
                "https://ia800404.us.archive.org/view_archive.php?archive=olcovers.zip&file=123-L.jpg"),
            "ia800404.us.archive.org" => Redirect(
                "https://ia800405.us.archive.org/view_archive.php?archive=olcovers.zip&file=123-L.jpg"),
            _ => new(HttpStatusCode.NotFound),
        });
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        result.Session!.Covers.Should().BeEmpty();
        result.SkippedCovers.Should().ContainSingle()
            .Which.Value.Should().Be("METADATA_COVER.HTTP_FAILED");
        handler.RequestCount.Should().Be(9);
        await result.Session.DisposeAsync();
        Directory.EnumerateDirectories(StagingRoot(temporary.Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task TransientAdditionalRedirectRestartsTrustedChain()
    {
        using TemporaryDirectory temporary = new();
        int cdnAttempt = 0;
        RecordingHandler handler = new(request => request.RequestUri!.Host switch
        {
            "covers.openlibrary.org" => Redirect(
                "https://archive.org/download/olcovers12/olcovers12-L.zip/123-L.jpg"),
            "archive.org" => Redirect(
                "https://ia800404.us.archive.org/view_archive.php?archive=olcovers.zip&file=123-L.jpg"),
            "ia800404.us.archive.org" => ++cdnAttempt == 1
                ? Redirect("https://ia800405.us.archive.org/unsupported")
                : Response(Jpeg(320, 480)),
            _ => new(HttpStatusCode.NotFound),
        });
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        handler.RequestCount.Should().Be(6);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task TransientHttpFailureRetriesBeforeStagingJpeg()
    {
        using TemporaryDirectory temporary = new();
        int attempt = 0;
        HttpResponseMessage handlerRequest() => ++attempt < 3
            ? new(HttpStatusCode.BadGateway)
            : Response(Jpeg(320, 480));
        RecordingHandler handler = new(_ => handlerRequest());
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        handler.RequestCount.Should().Be(3);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task PersistentTransientHttpFailureOmitsCoverAfterThreeAttempts()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => new(HttpStatusCode.BadGateway));
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        result.Session!.Covers.Should().BeEmpty();
        result.SkippedCovers.Should().ContainSingle()
            .Which.Value.Should().Be("METADATA_COVER.HTTP_FAILED");
        handler.RequestCount.Should().Be(3);
        await result.Session.DisposeAsync();
        Directory.EnumerateDirectories(StagingRoot(temporary.Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task TransientTimeoutRetriesBeforeStagingJpeg()
    {
        using TemporaryDirectory temporary = new();
        int attempt = 0;
        HttpResponseMessage handlerRequest() => ++attempt == 1
            ? throw new TaskCanceledException()
            : Response(Jpeg(320, 480));
        RecordingHandler handler = new(_ => handlerRequest());
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        handler.RequestCount.Should().Be(2);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task PersistentTransientTimeoutOmitsCoverAfterThreeAttempts()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => throw new TaskCanceledException());
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", Cover("https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        result.Session!.Covers.Should().BeEmpty();
        result.SkippedCovers.Should().ContainSingle()
            .Which.Value.Should().Be("METADATA_COVER.TIMEOUT");
        handler.RequestCount.Should().Be(3);
        await result.Session.DisposeAsync();
        Directory.EnumerateDirectories(StagingRoot(temporary.Path)).Should().BeEmpty();
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
        result.FailureCode.Should().Be("METADATA_COVER.RESPONSE_INVALID");
        handler.RequestCount.Should().Be(0);
        Directory.EnumerateDirectories(StagingRoot(temporary.Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task MismatchedSourceIdFailsWithoutHttpOrBatchResidue()
    {
        using TemporaryDirectory temporary = new();
        RecordingHandler handler = new(_ => Response(Jpeg(1, 1)));
        using BoundedEditionCoverStager stager = Stager(temporary.Path, handler);

        EditionCoverStagingResult result = await stager.StageAsync(
            [new("subject", new("456", "https://covers.openlibrary.org/b/id/123-L.jpg"))],
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        handler.RequestCount.Should().Be(0);
        Directory.EnumerateDirectories(StagingRoot(temporary.Path)).Should().BeEmpty();
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
        Directory.EnumerateDirectories(StagingRoot(temporary.Path)).Should().BeEmpty();
    }

    private static BoundedEditionCoverStager Stager(string root, HttpMessageHandler handler) => new(
        new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
        new()
        {
            StorageRoot = StagingRoot(root),
            CacheRoot = Path.Combine(root, "cache"),
            RetryDelay = TimeSpan.Zero,
        });

    private static string StagingRoot(string root) => Path.Combine(root, "staging");

    private static EditionCoverReference Cover(string url) => new("123", url);

    private static HttpResponseMessage Response(byte[] content)
    {
        ByteArrayContent body = new(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new(HttpStatusCode.OK) { Content = body };
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new(location, UriKind.Absolute) },
    };

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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
