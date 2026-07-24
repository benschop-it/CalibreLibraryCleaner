using System.Collections.Concurrent;
using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Hashing;

public sealed class StreamingSha256FormatFileHasherTests
{
    [Fact]
    public async Task HashSupportsEmptyFile()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "empty.epub");
        await File.WriteAllBytesAsync(path, []);
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();

        FormatHashResult result = (await hasher.HashAsync(
            [Request(0, path)],
            1,
            null,
            CancellationToken.None)).Single();

        result.Fingerprint!.SizeInBytes.Should().Be(0);
        result.Fingerprint.Sha256.Value.Should().Be(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant());
    }

    [Fact]
    public async Task HashStreamsKnownContentAndReportsFingerprint()
    {
        using TemporaryDirectory directory = new();
        byte[] content = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
        string path = Path.Combine(directory.Path, "book.epub");
        await File.WriteAllBytesAsync(path, content);
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();

        IReadOnlyList<FormatHashResult> results = await hasher.HashAsync(
            [Request(0, path)],
            1,
            null,
            CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(FormatHashResultStatus.Success);
        FormatFileFingerprint fingerprint = results[0].Fingerprint!;
        fingerprint.SizeInBytes.Should().Be(content.Length);
        fingerprint.Sha256.Value.Should().Be(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
    }

    [Fact]
    public async Task HashClassifiesMissingAndExclusivelyLockedFiles()
    {
        using TemporaryDirectory directory = new();
        string missing = Path.Combine(directory.Path, "missing.epub");
        string locked = Path.Combine(directory.Path, "locked.epub");
        await File.WriteAllBytesAsync(locked, [1, 2, 3]);
        await using FileStream lockStream = new(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();

        IReadOnlyList<FormatHashResult> results = await hasher.HashAsync(
            [Request(0, missing), Request(1, locked)],
            2,
            null,
            CancellationToken.None);

        results[0].Status.Should().Be(FormatHashResultStatus.Missing);
        results[1].Status.Should().Be(FormatHashResultStatus.Inaccessible);
        results.Select(result => result.Fingerprint).Should().OnlyContain(fingerprint => fingerprint == null);
    }

    [Fact]
    public async Task HashPreventsWriteWhileReading()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "changing.epub");
        await File.WriteAllBytesAsync(path, new byte[8 * 1024 * 1024]);
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();
        bool writeBlocked = false;
        InlineProgress progress = new(update =>
        {
            if (!writeBlocked && update.CompletedBytes > 0)
            {
                try
                {
                    File.AppendAllText(path, "changed");
                }
                catch (IOException)
                {
                    writeBlocked = true;
                }
            }
        });

        IReadOnlyList<FormatHashResult> results = await hasher.HashAsync(
            [Request(0, path)],
            1,
            progress,
            CancellationToken.None);

        writeBlocked.Should().BeTrue();
        results[0].Status.Should().Be(FormatHashResultStatus.Success);
        results[0].Fingerprint.Should().NotBeNull();
    }

    [Fact]
    public async Task HashRejectsFileWithLiveWriterEvenWhenLengthIsStable()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "writing.epub");
        await File.WriteAllBytesAsync(path, new byte[1024]);
        await using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        writer.Position = 0;
        await writer.WriteAsync(Enumerable.Repeat((byte)7, 1024).ToArray());
        await writer.FlushAsync();
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();

        FormatHashResult result = (await hasher.HashAsync(
            [Request(0, path)],
            1,
            null,
            CancellationToken.None)).Single();

        result.Status.Should().Be(FormatHashResultStatus.Inaccessible);
        result.Fingerprint.Should().BeNull();
    }

    [Fact]
    public async Task HashRejectsPathOutsideTrustedLibraryRoot()
    {
        using TemporaryDirectory directory = new();
        string libraryRoot = Path.Combine(directory.Path, "library");
        string outside = Path.Combine(directory.Path, "outside.epub");
        Directory.CreateDirectory(libraryRoot);
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();
        FormatHashRequest request = new(
            0,
            new CalibreBookId(1),
            "EPUB",
            new ResolvedFormatPath(libraryRoot, outside, "outside.epub"));

        FormatHashResult result = (await hasher.HashAsync(
            [request],
            1,
            null,
            CancellationToken.None)).Single();

        result.Status.Should().Be(FormatHashResultStatus.Inaccessible);
        result.ReasonCode.Should().Be("UnsafeManagedPath");
    }

    [Fact]
    public async Task HashRejectsReparsePointInParentChain()
    {
        using TemporaryDirectory directory = new();
        string libraryRoot = Path.Combine(directory.Path, "library");
        string outsideRoot = Path.Combine(directory.Path, "outside");
        string linkedDirectory = Path.Combine(libraryRoot, "Linked author");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(outsideRoot);
        string outsideFile = Path.Combine(outsideRoot, "Book.epub");
        await File.WriteAllBytesAsync(outsideFile, [1, 2, 3]);
        Directory.CreateSymbolicLink(linkedDirectory, outsideRoot);
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();
        FormatHashRequest request = new(
            0,
            new CalibreBookId(1),
            "EPUB",
            new ResolvedFormatPath(
                libraryRoot,
                Path.Combine(linkedDirectory, "Book.epub"),
                Path.Combine("Linked author", "Book.epub")));

        FormatHashResult result = (await hasher.HashAsync(
            [request],
            1,
            null,
            CancellationToken.None)).Single();

        result.Status.Should().Be(FormatHashResultStatus.Inaccessible);
        result.ReasonCode.Should().Be("UnsafeManagedPath");
    }

    [Fact]
    public async Task HashReturnsResultsInRequestSequenceOrder()
    {
        using TemporaryDirectory directory = new();
        string first = Path.Combine(directory.Path, "first.epub");
        string second = Path.Combine(directory.Path, "second.epub");
        await File.WriteAllBytesAsync(first, new byte[8 * 1024 * 1024]);
        await File.WriteAllBytesAsync(second, [1]);
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();

        IReadOnlyList<FormatHashResult> results = await hasher.HashAsync(
            [Request(0, first), Request(1, second)],
            2,
            null,
            CancellationToken.None);

        results.Select(result => result.Sequence).Should().Equal(0, 1);
    }

    [Fact]
    public async Task HashDoesNotReportEverySmallFileBoundary()
    {
        using TemporaryDirectory directory = new();
        FormatHashRequest[] requests = Enumerable.Range(0, 250)
            .Select(index =>
            {
                string path = Path.Combine(directory.Path, $"small-{index}.epub");
                File.WriteAllBytes(path, [unchecked((byte)index)]);
                return Request(index, path);
            })
            .ToArray();
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();
        List<FormatHashProgress> updates = [];

        IReadOnlyList<FormatHashResult> results = await hasher.HashAsync(
            requests,
            4,
            new InlineProgress(updates.Add),
            CancellationToken.None);

        results.Should().OnlyContain(result => result.Status == FormatHashResultStatus.Success);
        updates.Should().HaveCountLessThan(10);
        updates[^1].CompletedFiles.Should().Be(250);
    }

    [Fact]
    public async Task ExpectedFailureLogDoesNotContainPathOrExceptionDetails()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "private-author-title.epub");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        CapturingLoggerProvider loggerProvider = new();
        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddProvider(loggerProvider));
        services.AddCalibreLibraryInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();

        FormatHashResult result = (await hasher.HashAsync(
            [Request(0, path)],
            1,
            null,
            CancellationToken.None)).Single();

        result.Status.Should().Be(FormatHashResultStatus.Inaccessible);
        loggerProvider.Entries.Should().NotBeEmpty();
        loggerProvider.Entries.Should().OnlyContain(entry => entry.Exception == null);
        loggerProvider.Entries.Should().NotContain(entry => entry.Message.Contains(path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HashHonorsConcurrencyBoundAndCancellation()
    {
        using TemporaryDirectory directory = new();
        FormatHashRequest[] requests = Enumerable.Range(0, 4)
            .Select(index =>
            {
                string path = Path.Combine(directory.Path, $"book-{index}.epub");
                File.WriteAllBytes(path, new byte[4 * 1024 * 1024]);
                return Request(index, path);
            })
            .ToArray();
        using ServiceProvider provider = TestServices.CreateProvider();
        IFormatFileHasher hasher = provider.GetRequiredService<IFormatFileHasher>();
        using CancellationTokenSource cancellation = new();
        int maximumActive = 0;
        InlineProgress progress = new(update =>
        {
            maximumActive = Math.Max(maximumActive, update.ActiveFiles);
            if (update.CompletedBytes > 0)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = async () => await hasher.HashAsync(requests, 2, progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        maximumActive.Should().BeGreaterThan(1);
        maximumActive.Should().BeLessThanOrEqualTo(2);
        foreach (FormatHashRequest request in requests)
        {
            await using FileStream exclusive = new(
                request.Path.FullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
    }

    private static FormatHashRequest Request(int sequence, string path) => new(
        sequence,
        new CalibreBookId(sequence + 1),
        "EPUB",
        new ResolvedFormatPath(Path.GetDirectoryName(path)!, path, Path.GetFileName(path)));

    private sealed class InlineProgress(Action<FormatHashProgress> report) : IProgress<FormatHashProgress>
    {
        public void Report(FormatHashProgress value) => report(value);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentBag<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Add(new(formatter(state, exception), exception));
    }

    private sealed record LogEntry(string Message, Exception? Exception);
}
