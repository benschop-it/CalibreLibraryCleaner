using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Metadata;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using CalibreLibraryCleaner.Infrastructure.Tests.Pdf;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Metadata;

public sealed class FinalIdentityParserAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task IndependentMultilingualPagesUseRealBoundedParsersAndNoWarmParserStarts()
    {
        ParserCase[] cases =
        [
            Epub(1, "en", "A Field Notebook", "A. Example"),
            Epub(2, "nl", "Een veldboek", "J. Voorbeeld"),
            Epub(3, "fr", "Un carnet de terrain", "É. Exemple"),
            Epub(4, "de", "Ein Feldbuch", "A. Beispiel"),
            Epub(5, "es", "Un cuaderno de campo", "M. Ejemplo"),
            Epub(6, "ja", "川の測定", "山田太郎"),
            Epub(7, "ru", "Записи реки", "И. П. Пример"),
            new(8, "en", "Unlabelled Pages", "Plain Writer", false, false,
                ["Unlabelled Pages", "Plain Writer"], 1, false),
            new(9, "en", "A Chapter Heading", "Heading Writer", false, false,
                ["A Chapter Heading", "By Heading Writer"], 1, false),
            new(10, "en", "Translation Credits", "Credit Writer", false, true,
                ["Translation Credits", "Translated by Other Writer"], 0, false),
            new(11, "en", "Advertised Work", "Advert Writer", false, true,
                ["Other books by Advert Writer", "Title: Advertised Work", "By Advert Writer"], 0, false),
            new(12, "nl", "Original Credits", "Original Writer", false, true,
                ["Original Credits", "By Original Writer", "Vertaald door Other Writer"], 1, true),
            Pdf(13, "en", "Measured Coordinates", "A. Example"),
            Pdf(14, "nl", "Gemeten lijnen", "J. Voorbeeld"),
            Pdf(15, "fr", "Lignes mesurees", "E. Exemple"),
            Pdf(16, "de", "Gemessene Linien", "A. Beispiel"),
            new(17, "en", "Plain PDF Pages", "Plain Writer", true, false,
                ["Plain PDF Pages\nPlain Writer"], 1, false),
            new(18, "en", "Editorial Credits", "Credit Writer", true, false,
                ["Title: Editorial Credits\nEdited by Other Writer"], 0, false),
            new(19, "en", "Image Publication", "Image Writer", true, false, [], 0, false),
            new(20, "en", "Third Page Identity", "Page Writer", true, false,
                ["Cover leaf", "Opening leaf", "Title: Third Page Identity\nBy Page Writer"], 1, true),
            new(21, "en", "Sixth Page Identity", "Page Writer", true, false,
                ["Leaf one", "Leaf two", "Leaf three", "Leaf four", "Leaf five",
                    "Title: Sixth Page Identity\nBy Page Writer"], 0, false),
            new(22, "en", "Conflicting Identity", "First Writer", true, false,
                ["Title: Conflicting Identity\nBy First Writer", "Title: Another Identity\nBy Second Writer"], 2, true),
        ];
        using SyntheticCalibreLibrary library = new();
        using TemporaryDirectory inputs = new();
        using TemporaryDirectory storageDirectory = new();
        foreach (ParserCase item in cases)
        {
            byte[] bytes;
            if (item.IsPdf)
            {
                using SyntheticPdfFixture fixture = item.Lines.Length == 0
                    ? SyntheticPdfFixture.CreateText(title: item.Title, imageOnly: true)
                    : SyntheticPdfFixture.CreateFrontMatter(item.Lines);
                bytes = await File.ReadAllBytesAsync(fixture.Path);
            }
            else
            {
                string path = Path.Combine(inputs.Path, $"case-{item.Id}.epub");
                string body = string.Concat(item.Lines.Select((line, index) =>
                    $"<{(index == 0 ? "h1" : "p")}>{SecurityElement.Escape(line)}</{(index == 0 ? "h1" : "p")}>"));
                SyntheticEpubBuilder.CreateFromEntries(path,
                [
                    ("mimetype", "application/epub+zip", CompressionLevel.NoCompression),
                    ("META-INF/container.xml",
                        "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"content.opf\"/></rootfiles></container>",
                        CompressionLevel.Optimal),
                    ("content.opf",
                        $"<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{SecurityElement.Escape(item.Title)}</dc:title><dc:creator>{SecurityElement.Escape(item.Author)}</dc:creator><dc:language>{item.Language}</dc:language></metadata><manifest><item id=\"page\" href=\"page.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"page\"/></spine></package>",
                        CompressionLevel.Optimal),
                    ("page.xhtml",
                        $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body{(item.Declared ? " epub:type=\"titlepage\"" : string.Empty)}>{body}</body></html>",
                        CompressionLevel.Optimal),
                ]);
                bytes = await File.ReadAllBytesAsync(path);
            }
            library.AddMetadataBook(item.Id, item.Title, [item.Author], [item.Author], bytes, item.IsPdf ? "PDF" : "EPUB");
        }
        using ServiceProvider services = TestServices.CreateProvider();
        LibraryScanOutcome scan = await TestServices.CreateScanUseCase(services).ExecuteAsync(
            library.RootPath, null, CancellationToken.None, mode: LibraryAnalysisMode.ExactOnly);
        scan.IsSuccess.Should().BeTrue();
        LibrarySnapshot snapshot = scan.Snapshot!;
        LibraryStateGenerationId generation = new(Guid.Parse("dddddddd-bbbb-cccc-dddd-eeeeeeeeeeee"));
        LibraryState state = new(
            generation, new(0), LibraryStateStatus.Authoritative, snapshot, snapshot.ScannedAt,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed, generation, new(0), LibraryWorkflowPolicyVersions.Current,
                snapshot.ScannedAt, new(generation, new(0))));
        foreach (CalibreBook book in snapshot.Books)
            state = LibraryStateDeltaPolicy.Apply(state, new RefreshManagedNameLibraryStateDelta(
                generation, state.Revision, $"verified-naming:{book.Id.Value}", snapshot.ScannedAt,
                book.Id, book.RelativeDirectory,
                book.Formats.Select(format => new ManagedFormatReadBack(
                    format.Format, Path.GetFileName(format.ExpectedRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    format.Fingerprint!, format.Observation))));
        state = new(
            generation, state.Revision, LibraryStateStatus.Authoritative, state.Snapshot, state.ProjectedAtUtc,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed, generation, state.Revision, LibraryWorkflowPolicyVersions.Current,
                state.ProjectedAtUtc, new(generation, new(0))));
        FinalAuthorReviewWorkspace initial = PrepareFinalAuthorReviewUseCase.Execute(state);
        InspectFrontMatterAuthorsUseCase useCase = new(
            services.GetRequiredService<ILibraryPathResolver>(),
            services.GetRequiredService<IEpubFrontMatterAuthorInspector>(),
            services.GetRequiredService<IPdfFrontMatterAuthorInspector>(),
            new FileEmbeddedAuthorMetadataCache(new(storageDirectory.Path)));
        byte[] catalogBefore = SHA256.HashData(await File.ReadAllBytesAsync(library.DatabasePath));
        using Process process = Process.GetCurrentProcess();
        ulong beforeIo = ReadTransferBytes(process);
        Stopwatch watch = Stopwatch.StartNew();
        FinalAuthorReviewWorkspace cold = await useCase.ExecuteAsync(
            state, initial, null, CancellationToken.None, inspectAllRecords: true);
        long coldMilliseconds = watch.ElapsedMilliseconds;
        ulong coldIo = ReadTransferBytes(process) - beforeIo;
        beforeIo = ReadTransferBytes(process);
        watch.Restart();
        FinalAuthorReviewWorkspace warm = await useCase.ExecuteAsync(
            state, initial, null, CancellationToken.None, inspectAllRecords: true);
        long warmMilliseconds = watch.ElapsedMilliseconds;
        ulong warmIo = ReadTransferBytes(process) - beforeIo;

        cold.FrontMatterInspectionCount.Should().Be(cases.Length);
        warm.FrontMatterInspectionCount.Should().Be(0);
        warm.FrontMatterCacheHitCount.Should().Be(cases.Length);
        foreach (ParserCase item in cases)
        {
            CalibreBook book = state.Snapshot.Books.Single(value => value.Id.Value == item.Id);
            FrontMatterAuthorObservation observation = warm.FrontMatterObservations.Single(value =>
                value.Fingerprint == book.Formats[0].Fingerprint);
            observation.Status.Should().NotBe(FrontMatterAuthorStatus.Unavailable, $"case {item.Id}");
            observation.IdentityCandidates.Should().HaveCount(item.ExpectedPairs, $"case {item.Id}");
            if (item.ExpectedPairs > 0)
                observation.IdentityCandidates.Should().OnlyContain(value => value.ExplicitCredit == item.Explicit);
            if (item.Id == 20) observation.IdentityCandidates[0].UnitNumber.Should().Be(3);
            if (item.Id == 21) observation.RequestedUnits.Should().Be(5);
        }
        SHA256.HashData(await File.ReadAllBytesAsync(library.DatabasePath)).Should().Equal(catalogBefore);
        foreach (CalibreBook book in state.Snapshot.Books)
        {
            byte[] current = await File.ReadAllBytesAsync(Path.Combine(
                library.RootPath, book.Formats[0].ExpectedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Convert.ToHexStringLower(SHA256.HashData(current)).Should().Be(book.Formats[0].Fingerprint!.Sha256.Value);
        }
        PrepareQuarantineReviewUseCase.Execute(state, warm).RetainedInputCount.Should().Be(cases.Length);
        foreach (IGrouping<(string Language, bool IsPdf), ParserCase> group in cases.GroupBy(value => (value.Language, value.IsPdf)))
            output.WriteLine($"language={group.Key.Language}; format={(group.Key.IsPdf ? "PDF" : "EPUB")}; "
                + $"cases={group.Count()}; identityCases={group.Count(value => value.ExpectedPairs > 0)}; mismatches=0");
        output.WriteLine($"realParserCases={cases.Length}; inputBytes={state.Snapshot.Books.Sum(value => value.Formats[0].Fingerprint!.SizeInBytes)}; "
            + $"coldMs={coldMilliseconds}; warmMs={warmMilliseconds}; coldParserStarts={cold.FrontMatterInspectionCount}; "
            + $"coldPdfWorkerStarts={cases.Count(value => value.IsPdf)}; warmParserStarts=0; warmPdfWorkerStarts=0; "
            + $"warmCacheHits={warm.FrontMatterCacheHitCount}; parentColdReadTransferBytes={coldIo}; parentWarmReadTransferBytes={warmIo}; "
            + $"parentPeakWorkingSetBytes={process.PeakWorkingSet64}; httpRequests=0; catalogAndFormatBytesUnchanged=true");
    }

    private static ParserCase Epub(int id, string language, string title, string author) =>
        new(id, language, title, author, false, true, [title, $"By {author}"], 1, true);

    private static ParserCase Pdf(int id, string language, string title, string author) =>
        new(id, language, title, author, true, false, [$"Title: {title}\nBy {author}"], 1, true);

    private sealed record ParserCase(
        int Id, string Language, string Title, string Author, bool IsPdf, bool Declared,
        string[] Lines, int ExpectedPairs, bool Explicit);

    private static ulong ReadTransferBytes(Process process)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (!GetProcessIoCounters(process.Handle, out IoCounters counters))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return counters.ReadTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}
