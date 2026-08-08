using System.IO.Compression;
using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.Epub;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Epub;

public sealed class EpubContentSignatureInspectorTests
{
    [Fact]
    public async Task VisibleSpineTextProducesDeterministicBoundedLandmarks()
    {
        using TemporaryDirectory directory = new();
        string firstPath = Path.Combine(directory.Path, "First.epub");
        string secondPath = Path.Combine(directory.Path, "Second.epub");
        string visible = string.Join(' ', Enumerable.Range(0, 1_000).Select(value => $"token{value:D4}"));
        CreateEpub(firstPath, visible, "ignored script alpha", "ignored navigation alpha");
        CreateEpub(secondPath, visible, "different ignored script beta", "different ignored navigation beta");
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubContentSignatureInspector inspector = provider.GetRequiredService<IEpubContentSignatureInspector>();

        EpubContentSignature first = (await inspector.InspectContentSignatureAsync(
            new(await CreateRequestAsync(firstPath)), null, CancellationToken.None)).Signature!;
        EpubContentSignature second = (await inspector.InspectContentSignatureAsync(
            new(await CreateRequestAsync(secondPath)), null, CancellationToken.None)).Signature!;

        first.TotalTokenCount.Should().Be(1_000);
        first.Landmarks.Should().HaveCount(12);
        first.Landmarks.Should().OnlyContain(value => value.TokenCount == 64);
        first.Landmarks.Select(value => value.StrictHash)
            .Should().Equal(second.Landmarks.Select(value => value.StrictHash));
        first.Landmarks.Select(value => value.RelaxedHash)
            .Should().Equal(second.Landmarks.Select(value => value.RelaxedHash));
    }

    [Fact]
    public async Task RelaxedHashesIgnoreDiacriticsWhileStrictHashesRetainThem()
    {
        using TemporaryDirectory directory = new();
        string accentedPath = Path.Combine(directory.Path, "Accented.epub");
        string plainPath = Path.Combine(directory.Path, "Plain.epub");
        CreateEpub(accentedPath,
            string.Join(' ', Enumerable.Range(0, 1_000).Select(value => $"café{value:D4}")));
        CreateEpub(plainPath,
            string.Join(' ', Enumerable.Range(0, 1_000).Select(value => $"cafe{value:D4}")));
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubContentSignatureInspector inspector = provider.GetRequiredService<IEpubContentSignatureInspector>();

        EpubContentSignature accented = (await inspector.InspectContentSignatureAsync(
            new(await CreateRequestAsync(accentedPath)), null, CancellationToken.None)).Signature!;
        EpubContentSignature plain = (await inspector.InspectContentSignatureAsync(
            new(await CreateRequestAsync(plainPath)), null, CancellationToken.None)).Signature!;

        accented.Landmarks.Select(value => value.StrictHash)
            .Should().NotEqual(plain.Landmarks.Select(value => value.StrictHash));
        accented.Landmarks.Select(value => value.RelaxedHash)
            .Should().Equal(plain.Landmarks.Select(value => value.RelaxedHash));
    }

    [Fact]
    public async Task AddedFrontMatterRemainsHighSimilarityThroughBoundedShingleSketch()
    {
        using TemporaryDirectory directory = new();
        string baselinePath = Path.Combine(directory.Path, "Baseline.epub");
        string prefixedPath = Path.Combine(directory.Path, "Prefixed.epub");
        string body = string.Join(' ', Enumerable.Range(0, 2_000).Select(value => $"body{value:D4}"));
        string frontMatter = string.Join(' ', Enumerable.Range(0, 200).Select(value => $"front{value:D4}"));
        CreateEpub(baselinePath, body);
        CreateEpub(prefixedPath, frontMatter + " " + body);
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubContentSignatureInspector inspector = provider.GetRequiredService<IEpubContentSignatureInspector>();

        EpubContentSignature baseline = (await inspector.InspectContentSignatureAsync(
            new(await CreateRequestAsync(baselinePath)), null, CancellationToken.None)).Signature!;
        EpubContentSignature prefixed = (await inspector.InspectContentSignatureAsync(
            new(await CreateRequestAsync(prefixedPath)), null, CancellationToken.None)).Signature!;
        CandidateContentComparison comparison = EpubContentSignatureComparer.Compare(baseline, prefixed);

        baseline.ShingleMinHashes.Should().HaveCount(64);
        prefixed.ShingleMinHashes.Should().HaveCount(64);
        comparison.ShingleSimilarityPermille.Should().BeGreaterThanOrEqualTo(700);
        comparison.Classification.Should().Be(ContentSimilarityClassification.HighSimilarity);
    }

    [Fact]
    public async Task RepetitiveTextReturnsSafeInsufficientTextResult()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Repetitive.epub");
        CreateEpub(path, string.Join(' ', Enumerable.Repeat("same", 1_000)));
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubContentSignatureResult result = await provider.GetRequiredService<IEpubContentSignatureInspector>()
            .InspectContentSignatureAsync(new(await CreateRequestAsync(path)), null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ProblemCode.Should().Be(EpubContentSignatureProblemCode.InsufficientText);
    }

    [Fact]
    public async Task LandmarkPassRereadsOnlyIntersectingSpineChapters()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "ManyChapters.epub");
        const int chapterCount = 60;
        const int tokensPerChapter = 100;
        CreateMultiChapterEpub(path, chapterCount, tokensPerChapter);
        List<EpubContentSignatureProgress> progress = [];
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubContentSignatureResult result = await provider.GetRequiredService<IEpubContentSignatureInspector>()
            .InspectContentSignatureAsync(
                new(await CreateRequestAsync(path)),
                new InlineProgress(progress.Add),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Signature!.TotalTokenCount.Should().Be(chapterCount * tokensPerChapter);
        result.Signature.SampledChapterCount.Should().BeLessThan(chapterCount);
        progress.Should().Contain(value => value.Stage == "Counting" && value.TotalUnits == chapterCount);
        progress.Should().Contain(value =>
            value.Stage == "Sampling"
            && value.TotalUnits == result.Signature.SampledChapterCount);
        result.Signature.PolicyVersion.Should().Be(ContentSignaturePolicyVersion.Current);
    }

    [Fact]
    public async Task DiagnosticsReportExtractionStageTimingsWithoutPaths()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Diagnostics.epub");
        CreateEpub(path, string.Join(' ', Enumerable.Range(0, 1_000)
            .Select(value => $"token{value:D4}")));
        CapturingLogger<VersOneEpubInspector> logger = new();
        VersOneEpubInspector inspector = new(logger);

        EpubContentSignatureResult result = await inspector.InspectContentSignatureAsync(
            new(await CreateRequestAsync(path)), null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        logger.Messages.Should().Contain(value =>
            value.Contains("CountingMilliseconds=", StringComparison.Ordinal)
            && value.Contains("SamplingMilliseconds=", StringComparison.Ordinal));
        logger.Messages.Should().Contain(value =>
            value.Contains("PreflightMilliseconds=", StringComparison.Ordinal)
            && value.Contains("ExtractionMilliseconds=", StringComparison.Ordinal)
            && value.Contains("TotalMilliseconds=", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(value => value.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StaleObservationAndCancellationFailWithoutPartialSignature()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Book.epub");
        CreateEpub(path, string.Join(' ', Enumerable.Range(0, 1_000).Select(value => $"token{value:D4}")));
        EpubInspectionRequest current = await CreateRequestAsync(path);
        EpubInspectionRequest stale = current with
        {
            Observation = new(
                current.Observation.Length + 1,
                current.Observation.CreationTimeUtc,
                current.Observation.LastWriteTimeUtc,
                current.Observation.Attributes),
        };
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubContentSignatureInspector inspector = provider.GetRequiredService<IEpubContentSignatureInspector>();

        EpubContentSignatureResult changed = await inspector.InspectContentSignatureAsync(
            new(stale), null, CancellationToken.None);
        using CancellationTokenSource source = new();
        source.Cancel();
        Func<Task> cancelled = () => inspector.InspectContentSignatureAsync(new(current), null, source.Token);

        changed.Signature.Should().BeNull();
        changed.ProblemCode.Should().Be(EpubContentSignatureProblemCode.ChangedDuringInspection);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<EpubInspectionRequest> CreateRequestAsync(string path)
    {
        FileInfo info = new(path);
        await using FileStream stream = File.OpenRead(path);
        Sha256Digest digest = new(Convert.ToHexString(await SHA256.HashDataAsync(stream)));
        info.Refresh();
        return new(
            new(1),
            Path.GetDirectoryName(path)!,
            path,
            Path.GetFileName(path),
            new(info.Length, digest),
            new(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, (int)info.Attributes),
            EpubInspectionLimits.V1);
    }

    private static void CreateEpub(
        string path,
        string visibleText,
        string scriptText = "ignored script",
        string navigationText = "ignored navigation")
    {
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("mimetype", "application/epub+zip", CompressionLevel.NoCompression),
            ("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """, CompressionLevel.Optimal),
            ("OEBPS/content.opf", """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Fixture</dc:title></metadata>
                  <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """, CompressionLevel.Optimal),
            ("OEBPS/chapter.xhtml", $"<html><body><nav>{navigationText}</nav><script>{scriptText}</script><style>.x {{ content: 'ignored style'; }}</style><p>{visibleText}</p></body></html>", CompressionLevel.Optimal),
        ]);
    }

    private static void CreateMultiChapterEpub(string path, int chapterCount, int tokensPerChapter)
    {
        string manifest = string.Join(Environment.NewLine, Enumerable.Range(0, chapterCount)
            .Select(index => $"<item id=\"chapter{index}\" href=\"chapter{index}.xhtml\" media-type=\"application/xhtml+xml\"/>"));
        string spine = string.Join(Environment.NewLine, Enumerable.Range(0, chapterCount)
            .Select(index => $"<itemref idref=\"chapter{index}\"/>"));
        List<(string Name, string Content, CompressionLevel Compression)> entries =
        [
            ("mimetype", "application/epub+zip", CompressionLevel.NoCompression),
            ("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """, CompressionLevel.Optimal),
            ("OEBPS/content.opf", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Fixture</dc:title></metadata>
                  <manifest>{manifest}</manifest>
                  <spine>{spine}</spine>
                </package>
                """, CompressionLevel.Optimal),
        ];
        entries.AddRange(Enumerable.Range(0, chapterCount).Select(index =>
        {
            string text = string.Join(' ', Enumerable.Range(0, tokensPerChapter)
                .Select(token => $"chapter{index:D2}token{token:D3}"));
            return ($"OEBPS/chapter{index}.xhtml", $"<html><body><p>{text}</p></body></html>",
                CompressionLevel.Optimal);
        }));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
    }

    private sealed class InlineProgress(Action<EpubContentSignatureProgress> report) :
        IProgress<EpubContentSignatureProgress>
    {
        public void Report(EpubContentSignatureProgress value) => report(value);
    }

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
