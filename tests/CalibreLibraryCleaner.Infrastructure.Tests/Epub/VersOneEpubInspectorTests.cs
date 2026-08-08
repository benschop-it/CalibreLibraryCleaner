using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Xml;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Epub;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task LegacyCodePageDeclaredXmlIsDecodedWithoutDecoderFailure()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Legacy.epub");
        // content.opf declares windows-1252 and carries a raw 0xA4 byte (the ¤ sign
        // in that code page) that is invalid as UTF-8. The inspector must honor the
        // declared encoding instead of failing with a DecoderFallbackException.
        const string opfPrefix =
            "<?xml version=\"1.0\" encoding=\"windows-1252\"?>" +
            "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"book-id\">" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:identifier id=\"book-id\">9780306406157</dc:identifier><dc:title>Price ";
        const string opfSuffix =
            "</dc:title><dc:creator>Author</dc:creator><dc:language>en</dc:language><dc:date>2020-01-01</dc:date>" +
            "</metadata>" +
            "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>" +
            "<spine><itemref idref=\"chapter\"/></spine></package>";
        byte[] opf = [.. System.Text.Encoding.ASCII.GetBytes(opfPrefix), 0xA4, .. System.Text.Encoding.ASCII.GetBytes(opfSuffix)];
        SyntheticEpubBuilder.CreateFromRawEntries(path,
        [
            ("mimetype", System.Text.Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression),
            ("META-INF/container.xml", System.Text.Encoding.ASCII.GetBytes(
                "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"),
                CompressionLevel.Optimal),
            ("OEBPS/content.opf", opf, CompressionLevel.Optimal),
            ("OEBPS/chapter.xhtml", System.Text.Encoding.ASCII.GetBytes(
                $"<html><body><p>{new string('a', 6_000)}</p></body></html>"), CompressionLevel.Optimal),
        ]);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.PackageParsed.Should().BeTrue();
        result.EmbeddedTitle.Should().Be("Price \u00A4");
    }

    [Fact]
    public async Task MislabelledUtf8XmlWithInvalidBytesIsHandledGracefullyWithoutCrashing()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Mislabelled.epub");
        // Declares utf-8 but contains a stray 0xA4 byte that is not valid UTF-8. The
        // inspector must degrade to a structured result instead of letting a raw
        // DecoderFallbackException escape as an unhandled crash.
        const string opfPrefix =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"book-id\">" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:identifier id=\"book-id\">9780306406157</dc:identifier><dc:title>Price ";
        const string opfSuffix =
            "</dc:title><dc:creator>Author</dc:creator><dc:language>en</dc:language><dc:date>2020-01-01</dc:date>" +
            "</metadata>" +
            "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>" +
            "<spine><itemref idref=\"chapter\"/></spine></package>";
        byte[] opf = [.. System.Text.Encoding.ASCII.GetBytes(opfPrefix), 0xA4, .. System.Text.Encoding.ASCII.GetBytes(opfSuffix)];
        SyntheticEpubBuilder.CreateFromRawEntries(path,
        [
            ("mimetype", System.Text.Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression),
            ("META-INF/container.xml", System.Text.Encoding.ASCII.GetBytes(
                "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"),
                CompressionLevel.Optimal),
            ("OEBPS/content.opf", opf, CompressionLevel.Optimal),
            ("OEBPS/chapter.xhtml", System.Text.Encoding.ASCII.GetBytes(
                $"<html><body><p>{new string('a', 6_000)}</p></body></html>"), CompressionLevel.Optimal),
        ]);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        // The stray byte must not surface as an unhandled decoder exception; the call
        // completes and is either tolerated (parsed) or classified as a structured
        // problem.
        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        (result.PackageParsed || result.Problems.Count > 0).Should().BeTrue();
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

    [Fact]
    public async Task Epub2NcxExternalDoctypeIsAllowedWithoutFetchingIt()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "NcxDoctype.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"chapter\"/></spine>",
            packageVersion: "2.0").ToList();
        entries.Add(("OEBPS/toc.ncx", $"""
            <!DOCTYPE ncx SYSTEM "http://127.0.0.1:{port}/ncx.dtd">
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
              <head><meta name="dtb:uid" content="book-id"/></head>
              <docTitle><text>Synthetic</text></docTitle>
              <navMap><navPoint id="chapter"><navLabel><text>Chapter</text></navLabel><content src="chapter.xhtml"/></navPoint></navMap>
            </ncx>
            """, CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        listener.Pending().Should().BeFalse();
        result.Problems.Should().BeEmpty();
        result.PackageParsed.Should().BeTrue();
        result.NavigationPresent.Should().BeTrue();
    }

    [Theory]
    [InlineData("MissingContainer", EpubInspectionIssueCode.MissingContainer)]
    [InlineData("MalformedContainer", EpubInspectionIssueCode.MalformedContainer)]
    [InlineData("MissingPackage", EpubInspectionIssueCode.MissingPackage)]
    [InlineData("MalformedPackage", EpubInspectionIssueCode.MalformedPackage)]
    [InlineData("DtdContainer", EpubInspectionIssueCode.MalformedContainer)]
    public async Task InvalidContainerOrPackageUsesFallbackReadableContent(
        string scenario,
        EpubInspectionIssueCode expectedIssue)
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

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.Issues.Should().Contain(issue => issue.Code == expectedIssue);
        string.Join('|', result.Issues!.Select(issue => issue.Item)).Should().NotContain("file:///forbidden");
    }

    [Fact]
    public async Task MalformedContainerXmlReportsDistinctExplanation()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "MalformedContainerXml.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        // Binary/non-XML payload so parsing fails at the root (line 1, position 1),
        // mirroring the obfuscated toc.ncx that motivated the scoped XmlException.
        Replace(entries, "META-INF/container.xml", "\u0001\u0002not xml at all");
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.MalformedContainer);
    }

    [Fact]
    public async Task MalformedPackageXmlReportsDistinctExplanation()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "MalformedPackageXml.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        Replace(entries, "OEBPS/content.opf", "\u0001\u0002not xml at all");
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.MalformedPackage);
    }

    [Fact]
    public async Task MissingContainerUsesBoundedFallbackReadableContent()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Fallback.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("mimetype", "application/epub+zip", CompressionLevel.NoCompression),
            ("content/chapter.xhtml", $"<html><body><p>{new string('a', 6_000)}</p></body></html>", CompressionLevel.Optimal),
        ]);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.AvailableFacets.Should().HaveFlag(EpubAssessmentFacet.Content);
        result.ReadableCharacterCount.Should().Be(6_000);
        result.FallbackCandidateCount.Should().Be(1);
        result.FallbackRenderableCount.Should().Be(1);
        result.RenderableEvidence.Should().HaveFlag(EpubRenderableEvidence.Text);
        result.Issues.Should().ContainSingle(issue => issue.Code == EpubInspectionIssueCode.MissingContainer);
    }

    [Fact]
    public async Task MissingContainerWithoutRenderableContentRemainsIncomplete()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Incomplete.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("mimetype", "application/epub+zip", CompressionLevel.NoCompression),
            ("notes.txt", "not an EPUB content candidate", CompressionLevel.Optimal),
        ]);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.PackageMalformed);
        result.FallbackCandidateCount.Should().Be(0);
    }

    [Fact]
    public async Task FallbackNeverReadsContentProtectedByEncryptionMetadata()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "ProtectedFallback.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("META-INF/encryption.xml", "<encryption><EncryptedData><CipherData><CipherReference URI=\"protected.xhtml\"/></CipherData></EncryptedData></encryption>", CompressionLevel.Optimal),
            ("protected.xhtml", $"<html><body><p>{new string('z', 10_000)}</p></body></html>", CompressionLevel.Optimal),
            ("safe.xhtml", $"<html><body><p>{new string('a', 1_000)}</p></body></html>", CompressionLevel.Optimal),
        ]);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.AvailableFacets.Should().Be(EpubAssessmentFacet.Archive | EpubAssessmentFacet.Content);
        result.ReadableCharacterCount.Should().Be(1_000);
        result.Issues.Should().Contain(issue =>
            issue.Code == EpubInspectionIssueCode.EncryptedEntry && issue.Item == "protected.xhtml");
    }

    [Fact]
    public async Task FallbackStopsWhenEncryptionMetadataIsItselfEncrypted()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "UnreadableEncryptionMetadata.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("META-INF/encryption.xml", "<encryption><EncryptedData><CipherData><CipherReference URI=\"chapter.xhtml\"/></CipherData></EncryptedData></encryption>", CompressionLevel.NoCompression),
            ("chapter.xhtml", $"<html><body><p>{new string('a', 6_000)}</p></body></html>", CompressionLevel.Optimal),
        ]);
        MarkFirstEntryEncrypted(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.Encrypted);
        result.ReadableCharacterCount.Should().Be(0);
    }

    [Fact]
    public async Task TextlessFallbackReferenceRequiresAcceptedLocalMedia()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "NonMediaFallback.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("chapter.xhtml", "<html><body><img src=\"notes.txt\"/></body></html>", CompressionLevel.Optimal),
            ("notes.txt", "not renderable media", CompressionLevel.Optimal),
        ]);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
        result.FallbackRenderableCount.Should().Be(0);
        result.RenderableEvidence.Should().Be(EpubRenderableEvidence.None);
    }

    [Fact]
    public async Task NonRenderingDataAttributeCannotProveFallbackReadability()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "NonRenderingAttribute.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("chapter.xhtml", "<html><body><div data=\"cover.jpg\"></div></body></html>", CompressionLevel.Optimal),
            ("cover.jpg", "synthetic image bytes", CompressionLevel.NoCompression),
        ]);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
        result.RenderableEvidence.Should().Be(EpubRenderableEvidence.None);
    }

    [Fact]
    public async Task FallbackLocalReferenceLimitIsSharedAcrossCandidates()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "ReferenceBudget.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("chapter-1.xhtml", "<html><body><img src=\"cover-1.jpg\"/></body></html>", CompressionLevel.Optimal),
            ("chapter-2.xhtml", "<html><body><img src=\"cover-2.jpg\"/></body></html>", CompressionLevel.Optimal),
            ("cover-1.jpg", "synthetic image bytes", CompressionLevel.NoCompression),
            ("cover-2.jpg", "synthetic image bytes", CompressionLevel.NoCompression),
        ]);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumLocalReferences = 1 } },
            null,
            CancellationToken.None);

        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.FallbackRenderableCount.Should().Be(1);
        result.Issues.Should().Contain(issue =>
            issue.Code == EpubInspectionIssueCode.PartialCoverage
            && issue.Stage == "FallbackReferences"
            && issue.Observed == 2
            && issue.Limit == 1);
    }

    [Fact]
    public async Task SkippedOversizedFallbackCandidateRetainsWarningEvidence()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "OversizedFallback.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("large.xhtml", $"<html><body><p>{new string('z', 2_000)}</p></body></html>", CompressionLevel.NoCompression),
            ("safe.xhtml", $"<html><body><p>{new string('a', 500)}</p></body></html>", CompressionLevel.NoCompression),
        ]);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumChapterBytes = 1_000 } },
            null,
            CancellationToken.None);

        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.ReadableCharacterCount.Should().Be(500);
        result.Issues.Should().Contain(issue =>
            issue.Code == EpubInspectionIssueCode.OversizedEntry && issue.Item == "large.xhtml");
    }

    [Fact]
    public async Task MalformedNavigationXmlReportsDistinctExplanation()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "MalformedNavXml.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"chapter\"/></spine>").ToList();
        // The NCX is an eager XML reference; supply non-XML bytes so the scoped
        // XmlException classifies it as a malformed navigation document.
        entries.Add(("OEBPS/toc.ncx", "\u0001\u0002not xml at all", CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.RecoverableProblems.Should().ContainSingle(problem =>
            problem.Code == EpubInspectionProblemCode.PackageMalformed
            && problem.Explanation == "An EPUB navigation document is not valid XML.");
        result.PackageParsed.Should().BeTrue();
        result.ReadableCharacterCount.Should().Be(6_000);
    }

    [Fact]
    public async Task MalformedEncryptionXmlReportsDistinctExplanation()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "MalformedEncryptionXml.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        entries.Add(("META-INF/encryption.xml", "\u0001\u0002not xml at all", CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem =>
            problem.Code == EpubInspectionProblemCode.PackageMalformed
            && problem.Explanation == "The EPUB encryption document is not valid XML.");
    }

    [Fact]
    public async Task MalformedPackageIsStructuredProblemWithoutWarningLog()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "MalformedPackage.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        Replace(entries, "OEBPS/content.opf", "<package>");
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        CapturingLogger<VersOneEpubInspector> logger = new();
        VersOneEpubInspector inspector = new(logger);

        EpubInspectionResult result = await inspector.InspectAsync(
            await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.MalformedPackage);
        logger.Levels.Should().NotContain(LogLevel.Warning);
    }

    [Fact]
    public async Task EmptyManifestHrefIsSkippedAndReportedAsRecoverable()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "EmptyManifestHref.epub");
        SyntheticEpubBuilder.CreateFromEntries(path, StandardEntries(
            packageBody: "<manifest><item id=\"empty\" href=\"\" media-type=\"application/xhtml+xml\"/></manifest><spine/>"));
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubInspector inspector = provider.GetRequiredService<IEpubInspector>();
        int emptyKeyExceptions = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception is ArgumentException exception
                && string.Equals(exception.ParamName, "key", StringComparison.Ordinal)
                && exception.Message.Contains("Content file name cannot be empty", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref emptyKeyExceptions);
            }
        };
        AppDomain.CurrentDomain.FirstChanceException += handler;
        EpubInspectionResult result;
        try
        {
            result = await inspector.InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        result.Problems.Should().BeEmpty();
        result.RecoverableProblems.Should().ContainSingle(problem =>
            problem.Code == EpubInspectionProblemCode.PackageMalformed
            && problem.Explanation == "An EPUB manifest item has no content file path.");
        result.PackageParsed.Should().BeTrue();
        emptyKeyExceptions.Should().Be(0);
    }

    [Fact]
    public async Task NcxWithoutNavMapIsRecoverableWithoutVersOneExceptions()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "NcxWithoutNavMap.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries(
            packageBody: "<manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"chapter\"/></spine>").ToList();
        entries.Add(("OEBPS/toc.ncx", "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\"><head/></ncx>", CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        using ServiceProvider provider = TestServices.CreateProvider();
        IEpubInspector inspector = provider.GetRequiredService<IEpubInspector>();
        int ncxExceptions = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (string.Equals(args.Exception.GetType().FullName, "VersOne.Epub.Epub2NcxException", StringComparison.Ordinal)
                && args.Exception.Message.Contains("does not contain navMap", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ncxExceptions);
            }
        };
        AppDomain.CurrentDomain.FirstChanceException += handler;
        EpubInspectionResult result;
        try
        {
            result = await inspector.InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        result.Problems.Should().BeEmpty();
        result.RecoverableProblems.Should().ContainSingle(problem =>
            problem.Code == EpubInspectionProblemCode.PackageMalformed
            && problem.Explanation == "The EPUB 2 NCX document does not contain a navMap element.");
        result.PackageParsed.Should().BeTrue();
        result.NavigationPresent.Should().BeFalse();
        ncxExceptions.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedCompressionEntryIsSkippedDuringFallback()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "UnsupportedCompression.epub");
        SyntheticEpubBuilder.CreateFromEntries(path, StandardEntries());
        SetCompressionMethod(path, "OEBPS/content.opf", 99);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.UnsupportedEntry);
    }

    [Theory]
    [InlineData("../outside.xhtml")]
    [InlineData("OEBPS/nested/../outside.xhtml")]
    [InlineData("/absolute.xhtml")]
    [InlineData("folder\\ambiguous.xhtml")]
    [InlineData("C:/drive.xhtml")]
    public async Task UnsafeArchivePathsAreExcludedFromFallback(string unsafeName)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Unsafe.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        entries.Add((unsafeName, $"<html><body>{new string('z', 10_000)}</body></html>", CompressionLevel.NoCompression));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.ReadableCharacterCount.Should().Be(6_000);
        result.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.UnsafeEntry);
        File.Exists(Path.Combine(directory.Path, "outside.xhtml")).Should().BeFalse();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private static void SetCompressionMethod(string path, string entryName, ushort method)
    {
        byte[] archive = File.ReadAllBytes(path);
        bool localHeaderChanged = false;
        bool centralHeaderChanged = false;
        for (int index = 0; index <= archive.Length - 30; index++)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(index, 4));
            int nameOffset;
            int nameLengthOffset;
            int methodOffset;
            if (signature == 0x04034B50)
            {
                nameOffset = index + 30;
                nameLengthOffset = index + 26;
                methodOffset = index + 8;
            }
            else if (signature == 0x02014B50 && index <= archive.Length - 46)
            {
                nameOffset = index + 46;
                nameLengthOffset = index + 28;
                methodOffset = index + 10;
            }
            else
            {
                continue;
            }

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(nameLengthOffset, 2));
            if (nameOffset + nameLength > archive.Length
                || !System.Text.Encoding.UTF8.GetString(archive, nameOffset, nameLength).Equals(entryName, StringComparison.Ordinal))
            {
                continue;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(methodOffset, 2), method);
            localHeaderChanged |= signature == 0x04034B50;
            centralHeaderChanged |= signature == 0x02014B50;
        }

        if (!localHeaderChanged || !centralHeaderChanged)
        {
            throw new InvalidOperationException("The synthetic ZIP entry headers were not found.");
        }

        File.WriteAllBytes(path, archive);
    }

    [Fact]
    public async Task DuplicateCanonicalArchivePathsAreExcludedFromFallback()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Duplicate.epub");
        List<(string Name, string Content, CompressionLevel Compression)> entries = StandardEntries().ToList();
        entries.Add(("OEBPS/./chapter.xhtml", "duplicate", CompressionLevel.NoCompression));
        entries.Add(("fallback.xhtml", $"<html><body><p>{new string('b', 1_000)}</p></body></html>", CompressionLevel.Optimal));
        SyntheticEpubBuilder.CreateFromEntries(path, entries);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.ReadableCharacterCount.Should().Be(1_000);
        result.Issues.Should().ContainSingle(issue => issue.Code == EpubInspectionIssueCode.DuplicateEntry);
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
        file.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
        entries.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
        entry.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
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

        ratio.Problems.Should().BeEmpty();
        ratio.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        ratio.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.SuspiciousEntry);
        chapter.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        readable.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        references.Problems.Should().BeEmpty();
        references.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        references.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.PartialCoverage);
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

        xml.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        cover.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        spine.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
        spine.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        xml.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.PartialCoverage);
        cover.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.PartialCoverage);
        spine.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.PartialCoverage);
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
    public async Task CancellationDuringFallbackInspectionPropagatesAndLeavesLibraryUnchanged()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "FallbackCancel.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("chapter.xhtml", $"<html><body><p>{new string('a', 6_000)}</p></body></html>", CompressionLevel.Optimal),
        ]);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(directory.Path);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(update =>
        {
            if (update.Stage == "Fallback") cancellation.Cancel();
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
    public async Task FileTimestampChangeDuringFallbackInspectionDiscardsAllPartialFacts()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "FallbackChanged.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("chapter.xhtml", $"<html><body><p>{new string('a', 6_000)}</p></body></html>", CompressionLevel.Optimal),
        ]);
        EpubInspectionRequest request = await CreateRequestAsync(path);
        InlineProgress progress = new(update =>
        {
            if (update.Stage == "Fallback") File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        });
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(request, progress, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.ChangedDuringInspection);
        result.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
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
    public async Task NavigationIsSizeCheckedBeforeVersOneAndHarmlessDoctypeIsAllowed()
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
        int dtdXmlExceptions = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception is XmlException exception
                && exception.Message.Contains("DTD is prohibited", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref dtdXmlExceptions);
            }
        };
        AppDomain.CurrentDomain.FirstChanceException += handler;
        EpubInspectionResult dtd;
        try
        {
            dtd = await inspector.InspectAsync(await CreateRequestAsync(dtdPath), null, CancellationToken.None);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        oversized.Problems.Should().BeEmpty();
        oversized.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        oversized.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.PartialCoverage);
        dtd.Problems.Should().BeEmpty();
        dtd.PackageParsed.Should().BeTrue();
        dtdXmlExceptions.Should().Be(0);
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

        unsafeResult.Problems.Should().BeEmpty();
        unsafeResult.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        unsafeResult.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.InvalidManifestItemPath);

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
    public async Task DecodedHtmlCharacterLimitStopsMonolithicDictionaryChapterBeforeDomParsing()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "Dictionary.epub");
        SyntheticEpubBuilder.CreateFromEntries(path, StandardEntries(
            chapter: $"<html><body><p>{new string('x', 100_000)}</p></body></html>"));
        EpubInspectionRequest request = await CreateRequestAsync(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>().InspectAsync(
            request with { Limits = EpubInspectionLimits.V1 with { MaximumHtmlCharacters = 10_000 } },
            null,
            CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == EpubInspectionProblemCode.LimitExceeded);
        result.Coverage.Should().Be(EpubAssessmentCoverage.Incomplete);
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
            chapter: $"<html><body><a href=\"http://user:secret@127.0.0.1:{port}/book?token=secret\">remote</a><a href=\"http://www\u2028.sybex.com/book\">invalid host</a><img src=\"file:///C:/private/book.jpg\"/></body></html>"));
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        listener.Pending().Should().BeFalse();
        result.RemoteReferences.Should().Contain($"scheme:http;host:127.0.0.1");
        result.RemoteReferences.Should().Contain("scheme:http;host:www.sybex.com");
        result.RemoteReferences.Should().Contain("scheme:file");
        string.Join('|', result.RemoteReferences).Should().NotContainAny("secret", "C:/", "book.jpg", "\u2028");
    }

    [Fact]
    public async Task EncryptedZipEntryIsSkippedDuringFallback()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "ZipEncrypted.epub");
        SyntheticEpubBuilder.CreateFromEntries(path,
        [
            ("encrypted.xhtml", $"<html><body><p>{new string('z', 10_000)}</p></body></html>", CompressionLevel.NoCompression),
            ("fallback.xhtml", $"<html><body><p>{new string('a', 1_000)}</p></body></html>", CompressionLevel.Optimal),
        ]);
        MarkFirstEntryEncrypted(path);
        using ServiceProvider provider = TestServices.CreateProvider();

        EpubInspectionResult result = await provider.GetRequiredService<IEpubInspector>()
            .InspectAsync(await CreateRequestAsync(path), null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        result.ReadableCharacterCount.Should().Be(1_000);
        result.FallbackCandidateCount.Should().Be(1);
        result.FallbackRenderableCount.Should().Be(1);
        result.Issues.Should().Contain(issue => issue.Code == EpubInspectionIssueCode.EncryptedEntry);
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
        string? chapter = null,
        string packageVersion = "3.0")
    {
        yield return ("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        yield return ("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>", CompressionLevel.Optimal);
        yield return ("OEBPS/content.opf", $"""
            <package xmlns="http://www.idpf.org/2007/opf" version="{packageVersion}" unique-identifier="book-id">
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
