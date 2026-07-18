using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Epub;

public sealed class VersOneEpubInspectorTests
{
    [Fact]
    public async Task ValidSyntheticEpubIsInspectedWithoutExtraction()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Book.epub");
        SyntheticEpubBuilder.CreateValid(path);
        string[] before = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories);
        FileInfo info = new(path);
        await using FileStream digestStream = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(digestStream);
        EpubInspectionRequest request = new(
            new CalibreBookId(1), directory.Path, path, "Book.epub",
            new FormatFileFingerprint(info.Length, new Sha256Digest(Convert.ToHexString(digest).ToLowerInvariant())),
            new FormatFileObservation(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, (int)info.Attributes),
            EpubInspectionLimits.V1);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.PackageParsed.Should().BeTrue();
        result.ReadableCharacterCount.Should().BeGreaterThan(5_000);
        result.CoverWidth.Should().Be(800);
        result.CoverHeight.Should().Be(600);
        Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Should().Equal(before);
    }

    [Fact]
    public async Task MalformedZipBecomesStructuredFinding()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Bad.epub");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        FileInfo info = new(path);
        EpubInspectionRequest request = new(
            new CalibreBookId(1), directory.Path, path, "Bad.epub",
            new FormatFileFingerprint(info.Length, new Sha256Digest(new string('0', 64))),
            new FormatFileObservation(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, (int)info.Attributes), EpubInspectionLimits.V1);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.CannotOpen);
    }

    [Fact]
    public async Task StaleFileObservationDiscardsInspection()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Book.epub");
        SyntheticEpubBuilder.CreateValid(path);
        FileInfo info = new(path);
        EpubInspectionRequest request = new(
            new CalibreBookId(1), directory.Path, path, "Book.epub",
            new FormatFileFingerprint(info.Length, new Sha256Digest(new string('0', 64))),
            new FormatFileObservation(info.Length + 1, info.CreationTimeUtc, info.LastWriteTimeUtc, (int)info.Attributes), EpubInspectionLimits.V1);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.ChangedDuringInspection);
        result.PackageParsed.Should().BeFalse();
    }

    [Fact]
    public async Task MissingOptionalFeaturesAndStructuralDefectsRemainAssessableFacts()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Defects.epub");
        SyntheticEpubBuilder.CreateFromEntries(path, StandardEntries(
            packageBody: """
                <manifest>
                  <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                  <item id="missing" href="missing.xhtml" media-type="application/xhtml+xml"/>
                </manifest>
                <spine><itemref idref="chapter"/><itemref idref="chapter"/><itemref idref="missing"/></spine>
                """,
            chapter: "<html><body><p>tiny</p><img src=\"missing.png\"/><a href=\"https://example.invalid/book\">remote</a></body></html>"));
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.CoverPresent.Should().BeFalse();
        result.NavigationPresent.Should().BeFalse();
        result.MissingSpineResources.Should().Contain("missing");
        result.BrokenInternalReferences.Should().Contain("missing.png");
        result.EmptyChapters.Should().Contain("OEBPS/chapter.xhtml");
        result.RepeatedReferences.Should().Contain(item => item.StartsWith("idref:", StringComparison.Ordinal));
        result.RemoteReferences.Should().Contain("scheme:https;host:example.invalid");
    }

    [Fact]
    public async Task EmptyMetadataAndParsedEmptySpineRemainAssessable()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "EmptySpine.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("mimetype", "application/epub+zip", CompressionLevel.NoCompression),
            ("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"content.opf\"/></rootfiles></container>", CompressionLevel.Optimal),
            ("content.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata/><manifest/><spine/></package>", CompressionLevel.Optimal),
        ]);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.PackageParsed.Should().BeTrue();
        result.EmbeddedTitle.Should().BeNull();
        result.Authors.Should().BeEmpty();
        result.Languages.Should().BeEmpty();
        result.SpineItemCount.Should().Be(0);
    }

    [Fact]
    public async Task NavigationMustContainUsableTargetsAndReportsBrokenAndRepeatedTargets()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Navigation.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/></manifest><spine><itemref idref=\"chapter\"/></spine>").ToList();
        entries.Add(("OEBPS/nav.xhtml", "<html><body><nav><a href=\"chapter.xhtml#one\">One</a><a href=\"chapter.xhtml#two\">Two</a><a href=\"missing.xhtml\">Missing</a></nav></body></html>", CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.NavigationPresent.Should().BeTrue();
        result.BrokenInternalReferences.Should().Contain("missing.xhtml");
        result.RepeatedReferences.Should().Contain(item => item.StartsWith("navigation:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MissingContainer")]
    [InlineData("MalformedContainer")]
    [InlineData("MissingPackage")]
    [InlineData("MalformedPackage")]
    [InlineData("DtdContainer")]
    public async Task InvalidContainerOrPackageBecomesStructuredProblem(string scenario)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, $"{scenario}.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        if (scenario == "MissingContainer") entries.RemoveAll(entry => entry.Name == "META-INF/container.xml");
        if (scenario == "MalformedContainer") Replace(entries, "META-INF/container.xml", "<container>");
        if (scenario == "MissingPackage") entries.RemoveAll(entry => entry.Name == "OEBPS/content.opf");
        if (scenario == "MalformedPackage") Replace(entries, "OEBPS/content.opf", "<package>");
        if (scenario == "DtdContainer") Replace(entries, "META-INF/container.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///forbidden'>]><container>&e;</container>");
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.PackageMalformed);
        result.Problems.Select(problem => problem.Explanation).Should().NotContain(text => text.Contains("file:///forbidden", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../outside.xhtml")]
    [InlineData("OEBPS/nested/../outside.xhtml")]
    [InlineData("/absolute.xhtml")]
    [InlineData("folder\\ambiguous.xhtml")]
    [InlineData("C:/drive.xhtml")]
    public async Task UnsafeArchivePathsAreRejectedBeforeParsing(string unsafeName)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Unsafe.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        entries.Add((unsafeName, "unsafe", CompressionLevel.NoCompression));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.UnsafeArchive);
        File.Exists(Path.Combine(directory.Path, "outside.xhtml")).Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateCanonicalArchivePathsAreRejected()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Duplicate.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        entries.Add(("OEBPS/./chapter.xhtml", "duplicate", CompressionLevel.NoCompression));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.UnsafeArchive);
    }

    [Fact]
    public async Task ConfiguredFileEntryAndArchiveCountLimitsDisqualifyBeforeParserUse()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Limited.epub");
        SyntheticEpubBuilder.CreateValid(path);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubInspector inspector = provider.GetRequiredService<IEpubInspector>();

        EpubInspectionResult file = await inspector.InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumFileBytes = 1 } }, null, CancellationToken.None);
        EpubInspectionResult entries = await inspector.InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumArchiveEntries = 2 } }, null, CancellationToken.None);
        EpubInspectionResult entry = await inspector.InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumEntryBytes = 8 } }, null, CancellationToken.None);

        file.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        entries.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        entry.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
    }

    [Fact]
    public async Task CompressionRatioAndActualContentLimitsAreEnforced()
    {
        using TemporaryDirectory directory = new();
        string compressedPath = Path.Combine(directory.Path, "Compressed.epub");
        List<(string Name, string Content, CompressionLevel Compression)> compressedEntries = StandardEntries().ToList();
        compressedEntries.Add(("OEBPS/bomb.bin", new string('x', 2 * 1024 * 1024), CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(compressedPath, compressedEntries);
        string contentPath = Path.Combine(directory.Path, "Content.epub");
        SyntheticEpubBuilder.CreateFromEntries(contentPath, StandardEntries(
            chapter: "<html><body><p>abcdefghijklmnopqrstuvwxyz</p><img src=\"one.png\"/><img src=\"two.png\"/></body></html>"));
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubInspector inspector = provider.GetRequiredService<IEpubInspector>();

        EpubInspectionResult ratio = await inspector.InspectAsync(await CreateRequestAsync(compressedPath), null, CancellationToken.None);
        EpubInspectionRequest contentRequest = await CreateRequestAsync(contentPath);
        EpubInspectionResult chapter = await inspector.InspectAsync(
            contentRequest with { Limits = EpubInspectionLimits.V1 with { MaximumChapterBytes = 16 } }, null, CancellationToken.None);
        EpubInspectionResult readable = await inspector.InspectAsync(
            contentRequest with { Limits = EpubInspectionLimits.V1 with { MaximumReadableCharacters = 10 } }, null, CancellationToken.None);
        EpubInspectionResult references = await inspector.InspectAsync(
            contentRequest with { Limits = EpubInspectionLimits.V1 with { MaximumLocalReferences = 1 } }, null, CancellationToken.None);

        ratio.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.UnsafeArchive);
        chapter.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        readable.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        references.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
    }

    [Fact]
    public async Task AggregateCompressionRatioIsEnforcedIndependently()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "AggregateRatio.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        string compressible = new('x', 1024 * 1024);
        entries.AddRange(Enumerable.Range(0, 11)
            .Select(index => ($"OEBPS/payload-{index}.bin", compressible, CompressionLevel.Optimal)));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.UnsafeArchive);
    }

    [Fact]
    public async Task MalformedSupportedCoverHeaderIsReportedWithoutDecodingOrAllocation()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Cover.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"cover\" href=\"cover.png\" media-type=\"image/png\" properties=\"cover-image\"/></manifest><spine><itemref idref=\"chapter\"/></spine>").ToList();
        entries.Add(("OEBPS/cover.png", "not an image header", CompressionLevel.NoCompression));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.CoverPresent.Should().BeTrue();
        result.CoverWidth.Should().BeNull();
        result.CoverHeight.Should().BeNull();
        result.CoverHeaderMalformed.Should().BeTrue();
    }

    [Fact]
    public async Task XmlCoverSpineAndEvidenceLimitsAreAppliedIndependently()
    {
        using TemporaryDirectory directory = new();
        string validPath = Path.Combine(directory.Path, "Valid.epub");
        SyntheticEpubBuilder.CreateValid(validPath);
        string repeatedPath = Path.Combine(directory.Path, "Repeated.epub");
        SyntheticEpubBuilder.CreateFromEntries(repeatedPath, StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"chapter\"/><itemref idref=\"chapter\"/></spine>",
            chapter: "<html><body><img src=\"one\"/><img src=\"two\"/></body></html>"));
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubInspector inspector = provider.GetRequiredService<IEpubInspector>();
        EpubInspectionRequest valid = await CreateRequestAsync(validPath);
        EpubInspectionRequest repeated = await CreateRequestAsync(repeatedPath);

        EpubInspectionResult xml = await inspector.InspectAsync(
            valid with { Limits = EpubInspectionLimits.V1 with { MaximumXmlBytes = 32 } }, null, CancellationToken.None);
        EpubInspectionResult cover = await inspector.InspectAsync(
            valid with { Limits = EpubInspectionLimits.V1 with { MaximumCoverBytes = 10 } }, null, CancellationToken.None);
        EpubInspectionResult spine = await inspector.InspectAsync(
            repeated with { Limits = EpubInspectionLimits.V1 with { MaximumSpineItems = 1 } }, null, CancellationToken.None);
        EpubInspectionResult evidence = await inspector.InspectAsync(
            repeated with { Limits = EpubInspectionLimits.V1 with { MaximumEvidencePerRule = 1 } }, null, CancellationToken.None);

        xml.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        cover.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        spine.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        evidence.Problems.Should().BeEmpty();
        evidence.BrokenInternalReferences.Should().HaveCount(1);
        evidence.TotalBrokenInternalReferences.Should().Be(4);
    }

    [Theory]
    [InlineData("http://www.idpf.org/2008/embedding", false)]
    [InlineData("http://ns.adobe.com/pdf/enc#RC", false)]
    [InlineData("https://example.invalid/drm", true)]
    public async Task EncryptionIsClassifiedWithoutReadingOrFetchingProtectedContent(string algorithm, bool disqualified)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Encrypted.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        entries.Add(("META-INF/encryption.xml", $"<encryption><EncryptedData><EncryptionMethod Algorithm=\"{algorithm}\"/></EncryptedData></encryption>", CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        if (disqualified)
        {
            result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.Encrypted);
        }
        else
        {
            result.Problems.Should().BeEmpty();
            result.EncryptionState.Should().Be("Recognized font obfuscation");
        }
    }

    [Fact]
    public async Task CancellationDuringContentInspectionPropagatesAndLeavesLibraryUnchanged()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Cancel.epub");
        SyntheticEpubBuilder.CreateValid(path);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(directory.Path);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(update =>
        {
            if (update.Stage == "Content") cancellation.Cancel();
        });
        using ServiceProvider provider = TestServices.CreateProvider();

        Func<Task> act = async () => await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LibraryStateCapture.Capture(directory.Path).Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task FileTimestampChangeDuringContentInspectionDiscardsAllPartialFacts()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Changed.epub");
        SyntheticEpubBuilder.CreateValid(path);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        InlineProgress progress = new(update =>
        {
            if (update.Stage == "Content") File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        });
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, progress, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.ChangedDuringInspection);
        result.PackageParsed.Should().BeFalse();
        result.ReadableCharacterCount.Should().Be(0);
    }

    [Fact]
    public async Task DefaultEntryLimitRejectsCentralDirectoryBeforeArchiveInspection()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "ManyEntries.epub");
        SyntheticEpubBuilder.CreateFromEntries(
            path,
            Enumerable.Range(0, EpubInspectionLimits.V1.MaximumArchiveEntries + 1)
                .Select(index => ($"entry-{index:D5}", string.Empty, CompressionLevel.NoCompression)));
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
    }

    [Fact]
    public async Task NavigationIsSizeAndDtdCheckedBeforeVersOne()
    {
        using TemporaryDirectory directory = new();
        string oversizedPath = Path.Combine(directory.Path, "OversizedNav.epub");
        List<(string Name, string Content, CompressionLevel Compression)> oversizedEntries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/></manifest><spine><itemref idref=\"chapter\"/></spine>").ToList();
        oversizedEntries.Add(("OEBPS/nav.xhtml", $"<html><body>{new string(' ', 2_000)}</body></html>", CompressionLevel.NoCompression));
        SyntheticEpubBuilder.CreateFromEntries(oversizedPath, oversizedEntries);
        string dtdPath = Path.Combine(directory.Path, "DtdNav.epub");
        List<(string Name, string Content, CompressionLevel Compression)> dtdEntries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/></manifest><spine><itemref idref=\"chapter\"/></spine>").ToList();
        dtdEntries.Add(("OEBPS/nav.xhtml", "<!DOCTYPE html><html><body><nav><ol/></nav></body></html>", CompressionLevel.NoCompression));
        SyntheticEpubBuilder.CreateFromEntries(dtdPath, dtdEntries);
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubInspector inspector = provider.GetRequiredService<IEpubInspector>();

        EpubInspectionRequest oversizedRequest = await CreateRequestAsync(oversizedPath);
        EpubInspectionResult oversized = await inspector.InspectAsync(
            oversizedRequest with { Limits = EpubInspectionLimits.V1 with { MaximumXmlBytes = 1_024 } }, null, CancellationToken.None);
        EpubInspectionResult dtd = await inspector.InspectAsync(await CreateRequestAsync(dtdPath), null, CancellationToken.None);

        oversized.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        dtd.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.PackageMalformed);
    }

    [Fact]
    public async Task RootRelativeNavigationPathAndReparseLibraryRootAreRejected()
    {
        using TemporaryDirectory directory = new();
        string unsafePath = Path.Combine(directory.Path, "RootRelative.epub");
        SyntheticEpubBuilder.CreateFromEntries(unsafePath, StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"nav\" href=\"/nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/></manifest><spine><itemref idref=\"chapter\"/></spine>"));
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubInspector inspector = provider.GetRequiredService<IEpubInspector>();

        EpubInspectionResult unsafeResult = await inspector.InspectAsync(await CreateRequestAsync(unsafePath), null, CancellationToken.None);

        unsafeResult.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.UnsafeArchive);

        string actualRoot = Path.Combine(directory.Path, "actual");
        string linkedRoot = Path.Combine(directory.Path, "linked");
        Directory.CreateDirectory(actualRoot);
        string actualPath = Path.Combine(actualRoot, "Book.epub");
        SyntheticEpubBuilder.CreateValid(actualPath);
        Directory.CreateSymbolicLink(linkedRoot, actualRoot);
        EpubInspectionRequest linkedRequest = await CreateRequestAsync(Path.Combine(linkedRoot, "Book.epub"));
        EpubInspectionResult linked = await inspector.InspectAsync(linkedRequest, null, CancellationToken.None);

        linked.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.ChangedDuringInspection);
    }

    [Fact]
    public async Task HtmlStructureLimitDisqualifiesBeforeLargeDomTraversal()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "ManyNodes.epub");
        SyntheticEpubBuilder.CreateFromEntries(path, StandardEntries(
            chapter: $"<html><body>{string.Concat(Enumerable.Repeat("<b>x</b>", 100))}</body></html>"));
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumHtmlNodes = 20 } }, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
    }

    [Fact]
    public async Task OversizedOptionalCssIsReportedAsTruncationWithoutAFalseBrokenReference()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "LargeCss.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"styles.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"chapter\"/></spine>").ToList();
        entries.Add(("OEBPS/styles.css", new string('x', 2_000), CompressionLevel.NoCompression));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumCssBytes = 1_024 } }, null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.AnalysisTruncated.Should().BeTrue();
        result.OptionalTruncations.Should().Contain("css:OEBPS/styles.css");
        result.BrokenInternalReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalReferencesAreNotFetchedAndSensitivePartsAreNotRetained()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "External.epub");
        SyntheticEpubBuilder.CreateFromEntries(path, StandardEntries(
            chapter: $"<html><body><a href=\"http://user:secret@127.0.0.1:{port}/book?token=secret\">remote</a><img src=\"file:///C:/private/book.jpg\"/></body></html>"));
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        listener.Pending().Should().BeFalse();
        result.RemoteReferences.Should().Contain($"scheme:http;host:127.0.0.1");
        result.RemoteReferences.Should().Contain("scheme:file");
        string.Join('|', result.RemoteReferences).Should().NotContainAny("secret", "C:/", "book.jpg");
    }

    [Fact]
    public async Task EncryptedZipFlagIsClassifiedBeforeEntryReads()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "ZipEncrypted.epub");
        SyntheticEpubBuilder.CreateValid(path);
        MarkFirstEntryEncrypted(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.Encrypted);
    }

    private static async Task<EpubInspectionRequest> CreateRequestAsync(string path)
    {
        FileInfo info = new(path);
        await using FileStream digestStream = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(digestStream);
        return new(
            new CalibreBookId(1),
            Path.GetDirectoryName(path)!,
            path,
            Path.GetFileName(path),
            new FormatFileFingerprint(info.Length, new Sha256Digest(Convert.ToHexString(digest).ToLowerInvariant())),
            new FormatFileObservation(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, (int)info.Attributes),
            EpubInspectionLimits.V1);
    }

    private static IEnumerable<(string Name, string Content, CompressionLevel Compression)> StandardEntries(
        string? packageBody = null,
        string? chapter = null)
    {
        yield return ("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        yield return ("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>", CompressionLevel.Optimal);
        yield return ("OEBPS/content.opf", $"""
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">9780306406157</dc:identifier><dc:title>Synthetic</dc:title>
                <dc:creator>Author</dc:creator><dc:language>en</dc:language><dc:date>2020-01-01</dc:date>
              </metadata>
              {packageBody ?? "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"chapter\"/></spine>"}
            </package>
            """, CompressionLevel.Optimal);
        yield return ("OEBPS/chapter.xhtml", chapter ?? $"<html><body><p>{new string('a', 6_000)}</p></body></html>", CompressionLevel.Optimal);
    }

    private static void Replace(
        List<(string Name, string Content, CompressionLevel Compression)> entries,
        string name,
        string content)
    {
        int index = entries.FindIndex(entry => entry.Name == name);
        entries[index] = (name, content, CompressionLevel.Optimal);
    }

    private static void MarkFirstEntryEncrypted(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int localHeader = FindSignature(bytes, 0x04034B50);
        int centralHeader = FindSignature(bytes, 0x02014B50);
        bytes[localHeader + 6] |= 0x01;
        bytes[centralHeader + 8] |= 0x01;
        File.WriteAllBytes(path, bytes);
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (int index = 0; index <= bytes.Length - sizeof(uint); index++)
        {
            if (BitConverter.ToUInt32(bytes, index) == signature)
            {
                return index;
            }
        }

        throw new InvalidOperationException("ZIP signature was not found in the synthetic fixture.");
    }

    private sealed class InlineProgress(Action<EpubInspectionProgress> report) : IProgress<EpubInspectionProgress>
    {
        public void Report(EpubInspectionProgress value) => report(value);
    }
}
