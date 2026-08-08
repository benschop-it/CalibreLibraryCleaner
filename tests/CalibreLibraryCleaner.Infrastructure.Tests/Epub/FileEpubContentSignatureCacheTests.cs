using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.Epub;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Epub;

public sealed class FileEpubContentSignatureCacheTests
{
    [Fact]
    public async Task SignatureRoundTripsWithoutPathsOrProse()
    {
        using TemporaryDirectory directory = new();
        FileEpubContentSignatureCache cache = new(new(directory.Path));
        EpubContentSignatureRequest request = Request(new string('a', 64));
        EpubContentSignatureCacheKey key = EpubContentSignatureCacheKey.Create(request);
        EpubContentSignature signature = Signature(request.Source.Fingerprint);

        await cache.WriteAsync(key, signature, CancellationToken.None);
        EpubContentSignature? loaded = await cache.TryReadAsync(key, CancellationToken.None);

        loaded.Should().BeEquivalentTo(signature);
        string json = await File.ReadAllTextAsync(Directory.GetFiles(directory.Path, "*.json").Single());
        json.Should().NotContain(request.Source.FullPath);
        json.Should().NotContain("sample sentence");
        json.ToLowerInvariant().Should().NotContain("title");
    }

    [Fact]
    public async Task CorruptOrIncompleteEntryBecomesCacheMiss()
    {
        using TemporaryDirectory directory = new();
        FileEpubContentSignatureCache cache = new(new(directory.Path));
        EpubContentSignatureRequest request = Request(new string('b', 64));
        EpubContentSignatureCacheKey key = EpubContentSignatureCacheKey.Create(request);
        Directory.CreateDirectory(directory.Path);
        string path = Path.Combine(directory.Path, key.Value + ".json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":\"epub-content-signature-cache/1.0\"}");

        EpubContentSignature? loaded = await cache.TryReadAsync(key, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public void KeyChangesWithFingerprintAndResourcePolicy()
    {
        EpubContentSignatureRequest baseline = Request(new string('c', 64));
        EpubContentSignatureRequest changedFingerprint = Request(new string('d', 64));
        EpubContentSignatureRequest changedLandmarks = new(
            baseline.Source,
            new(landmarkCount: 10));

        EpubContentSignatureCacheKey.Create(baseline).Value.Should()
            .NotBe(EpubContentSignatureCacheKey.Create(changedFingerprint).Value)
            .And.NotBe(EpubContentSignatureCacheKey.Create(changedLandmarks).Value);
    }

    [Fact]
    public async Task CancelledWritePublishesNoEntry()
    {
        using TemporaryDirectory directory = new();
        FileEpubContentSignatureCache cache = new(new(directory.Path));
        EpubContentSignatureRequest request = Request(new string('e', 64));
        using CancellationTokenSource source = new();
        source.Cancel();

        Func<Task> act = () => cache.WriteAsync(
            EpubContentSignatureCacheKey.Create(request),
            Signature(request.Source.Fingerprint),
            source.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(directory.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task WritesDoNotPruneQuadraticallyAndBatchPruneReportsMetrics()
    {
        using TemporaryDirectory directory = new();
        CapturingLogger<FileEpubContentSignatureCache> logger = new();
        FileEpubContentSignatureCache cache = new(new(
            directory.Path,
            maximumEntryBytes: 64 * 1024,
            maximumTotalBytes: 64 * 1024), logger);
        const int entryCount = 40;
        for (int index = 0; index < entryCount; index++)
        {
            string digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(index.ToString(CultureInfo.InvariantCulture))));
            EpubContentSignatureRequest request = Request(digest);
            await cache.WriteAsync(
                EpubContentSignatureCacheKey.Create(request),
                Signature(request.Source.Fingerprint),
                CancellationToken.None);
        }

        Directory.GetFiles(directory.Path, "*.json").Should().HaveCount(entryCount);
        logger.Messages.Should().NotContain(value => value.Contains("cache prune", StringComparison.OrdinalIgnoreCase));

        await cache.PruneAsync(CancellationToken.None);

        Directory.GetFiles(directory.Path, "*.json").Should().HaveCountLessThan(entryCount);
        logger.Messages.Should().ContainSingle(value =>
            value.Contains("EntriesScanned=40", StringComparison.Ordinal)
            && value.Contains("EntriesRemoved=", StringComparison.Ordinal)
            && value.Contains("RootSegmentsChecked=", StringComparison.Ordinal)
            && value.Contains("TotalMilliseconds=", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(value => value.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FullRootValidationAndControlledLeafValidationHaveDifferentScopes()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "entry.json");
        await File.WriteAllTextAsync(path, "{}");

        bool fullAccepted = ExecutionPathGuard.TryRejectReparsePoints(
            path,
            leafExists: true,
            out string? fullReason,
            out ExecutionPathGuard.ReparsePointCheckMetrics metrics);
        bool leafAccepted = ExecutionPathGuard.TryRejectReparsePointLeaf(
            path,
            leafExists: true,
            out string? leafReason);

        fullAccepted.Should().BeTrue(fullReason);
        metrics.SegmentsChecked.Should().BeGreaterThan(1);
        leafAccepted.Should().BeTrue(leafReason);
    }

    private static EpubContentSignatureRequest Request(string digest)
    {
        FormatFileFingerprint fingerprint = new(1_024, new(digest));
        return new(new(
            new(1),
            "C:\\Library",
            "C:\\Library\\Book.epub",
            "Book.epub",
            fingerprint,
            new(1_024, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0),
            EpubInspectionLimits.V1));
    }

    private static EpubContentSignature Signature(FormatFileFingerprint fingerprint) => new(
        fingerprint,
        1_000,
        1,
        1,
        Enumerable.Range(0, 12).Select(index => new ContentLandmarkSignature(
            index,
            index * 70,
            64,
            32,
            new(string.Concat(Enumerable.Repeat(index.ToString("x2", CultureInfo.InvariantCulture), 32))),
            new(string.Concat(Enumerable.Repeat(index.ToString("x2", CultureInfo.InvariantCulture), 32))))));

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
