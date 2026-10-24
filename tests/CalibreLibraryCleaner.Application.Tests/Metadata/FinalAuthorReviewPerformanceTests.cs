using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class FinalAuthorReviewPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CompleteRetainedRecordPassDeduplicatesColdDemandAndStartsNoWarmParsers()
    {
        const int recordCount = 2_000;
        const int distinctFormats = 20;
        const string root = "C:\\generated-library";
        CalibreBook[] books = Enumerable.Range(1, recordCount).Select(id =>
        {
            FormatFileFingerprint fingerprint = Fingerprint(id % distinctFormats);
            return new CalibreBook(
                new(id), $"Generated Title {id}", "Reliable Writer",
                [new(new(id), "Reliable Writer", "Writer, Reliable")], [],
                [new("EPUB", "book", $"Writer/Book ({id})/book.epub", FormatFileStatus.Present,
                    fingerprint, new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
                $"Writer/Book ({id})");
        }).ToArray();
        LibraryStateGenerationId generation = new(Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"));
        LibraryState state = new(
            generation, new(0), LibraryStateStatus.Authoritative,
            new(new("generated", recordCount, root), DateTimeOffset.UnixEpoch, books, []),
            DateTimeOffset.UnixEpoch, workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed, generation, new(0), LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch, new(generation, new(0))));
        FinalAuthorReviewWorkspace initial = PrepareFinalAuthorReviewUseCase.Execute(state);
        initial.Subjects.Should().BeEmpty("ordinary catalog names are not independent qualification evidence");
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        A.CallTo(() => resolver.ValidateAsync(root, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(new(root, $"{root}\\metadata.db")));
        A.CallTo(() => resolver.ResolveFormat(
                A<ValidatedLibraryLocation>._, A<string>._, A<string>._, "EPUB"))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                return ResolvedFormatPathOutcome.Success(new(
                    root, $"{root}\\{directory.Replace('/', '\\')}\\book.epub", $"{directory}/book.epub"));
            });
        Dictionary<FrontMatterAuthorCacheKey, FrontMatterAuthorObservation> values = [];
        IFrontMatterAuthorCache cache = A.Fake<IFrontMatterAuthorCache>();
        A.CallTo(() => cache.TryReadAsync(A<FrontMatterAuthorCacheKey>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(values.GetValueOrDefault(
                call.GetArgument<FrontMatterAuthorCacheKey>(0)!)));
        A.CallTo(() => cache.WriteAsync(A<FrontMatterAuthorObservation>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                FrontMatterAuthorObservation value = call.GetArgument<FrontMatterAuthorObservation>(0)!;
                values[new(value.Format, value.Fingerprint, value.ExtractorVersion, value.LimitsVersion)] = value;
            }).Returns(Task.CompletedTask);
        IEpubFrontMatterAuthorInspector epub = A.Fake<IEpubFrontMatterAuthorInspector>();
        A.CallTo(() => epub.InspectFrontMatterAuthorsAsync(
                A<EpubInspectionRequest>._, A<FrontMatterInspectionLimits>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(new FrontMatterAuthorObservation(
                "EPUB", call.GetArgument<EpubInspectionRequest>(0)!.Fingerprint,
                FrontMatterAuthorVersions.EpubExtractor, FrontMatterAuthorVersions.Limits,
                FrontMatterAuthorStatus.Missing, [], 1, 1, 100, false)));
        InspectFrontMatterAuthorsUseCase useCase = new(
            resolver, epub, A.Fake<IPdfFrontMatterAuthorInspector>(), cache);

        Stopwatch timer = Stopwatch.StartNew();
        FinalAuthorReviewWorkspace cold = await useCase.ExecuteAsync(
            state, initial, null, CancellationToken.None, inspectAllRecords: true);
        long coldMilliseconds = timer.ElapsedMilliseconds;
        timer.Restart();
        FinalAuthorReviewWorkspace warm = await useCase.ExecuteAsync(
            state, initial, null, CancellationToken.None, inspectAllRecords: true);
        long warmMilliseconds = timer.ElapsedMilliseconds;

        cold.FrontMatterObservations.Should().HaveCount(recordCount);
        cold.FrontMatterInspectionCount.Should().Be(distinctFormats);
        warm.FrontMatterInspectionCount.Should().Be(0);
        warm.FrontMatterCacheHitCount.Should().Be(recordCount);
        PrepareQuarantineReviewUseCase.Execute(state, warm).RetainedInputCount.Should().Be(recordCount);
        A.CallTo(() => epub.InspectFrontMatterAuthorsAsync(
            A<EpubInspectionRequest>._, A<FrontMatterInspectionLimits>._, A<CancellationToken>._))
            .MustHaveHappened(distinctFormats, Times.Exactly);
        using Process process = Process.GetCurrentProcess();
        output.WriteLine(
            $"syntheticRecords={recordCount}; distinctFormats={distinctFormats}; coldMs={coldMilliseconds}; "
            + $"warmMs={warmMilliseconds}; coldParserStarts={distinctFormats}; warmParserStarts=0; "
            + $"warmCacheHits={recordCount}; HTTPRequests=0; "
            + $"processPeakBytes={process.PeakWorkingSet64}; adapter=fake");
    }

    [Fact]
    public async Task ColdAndWarmDemandScalesWithSuspectRecordsOnly()
    {
        const int recordCount = 5_000;
        const int suspectCount = 200;
        const string root = "C:\\generated-library";
        CalibreBook[] books = Enumerable.Range(1, recordCount).Select(id =>
        {
            bool suspect = id <= suspectCount;
            string author = suspect ? "Unknown" : $"Reliable Writer {NumberWord(id)}";
            if (!suspect)
                return new CalibreBook(
                    new(id), $"Generated Title {id}", author,
                    [new(new(id), author, author)], [], [], $"Author/Book ({id})");
            FormatFileFingerprint fingerprint = Fingerprint(id);
            string format = id % 2 == 0 ? "PDF" : "EPUB";
            string extension = format.ToLowerInvariant();
            return new CalibreBook(
                new(id), $"Generated Title {id}", author,
                [new(new(id), author, author)], [],
                [new(
                    format, $"book-{id}", $"Unknown/Book ({id})/book-{id}.{extension}",
                    FormatFileStatus.Present, fingerprint,
                    new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
                $"Unknown/Book ({id})");
        }).ToArray();
        LibrarySnapshot snapshot = new(
            new("generated", recordCount, root), DateTimeOffset.UnixEpoch, books, []);
        LibraryStateGenerationId generation = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        LibraryState state = new(
            generation, new(0), LibraryStateStatus.Authoritative, snapshot,
            DateTimeOffset.UnixEpoch,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed, generation, new(0),
                LibraryWorkflowPolicyVersions.Current, DateTimeOffset.UnixEpoch,
                new(generation, new(0))));
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        A.CallTo(() => resolver.ValidateAsync(root, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(new(root, $"{root}\\metadata.db")));
        A.CallTo(() => resolver.ResolveFormat(
                A<ValidatedLibraryLocation>._, A<string>._, A<string>._, A<string>._))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                string name = call.GetArgument<string>(2)!;
                string extension = call.GetArgument<string>(3)!.ToLowerInvariant();
                return ResolvedFormatPathOutcome.Success(new(
                    root, $"{root}\\{directory.Replace('/', '\\')}\\{name}.{extension}",
                    $"{directory}/{name}.{extension}"));
            });
        Dictionary<EmbeddedAuthorMetadataCacheKey, EmbeddedAuthorMetadataObservation> cacheValues = [];
        IEmbeddedAuthorMetadataCache cache = A.Fake<IEmbeddedAuthorMetadataCache>();
        A.CallTo(() => cache.TryReadAsync(
                A<EmbeddedAuthorMetadataCacheKey>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(cacheValues.GetValueOrDefault(
                call.GetArgument<EmbeddedAuthorMetadataCacheKey>(0)!)));
        A.CallTo(() => cache.WriteAsync(
                A<EmbeddedAuthorMetadataObservation>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                EmbeddedAuthorMetadataObservation value = call.GetArgument<EmbeddedAuthorMetadataObservation>(0)!;
                cacheValues[value.Key] = value;
            })
            .Returns(Task.CompletedTask);
        int inspections = 0;
        int pdfWorkerLaunches = 0;
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        A.CallTo(() => inspector.InspectAuthorMetadataAsync(
                A<EpubInspectionRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                inspections++;
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                return Task.FromResult(new EmbeddedAuthorMetadataObservation(
                    new(
                        "EPUB", request.Fingerprint,
                        EmbeddedAuthorMetadataVersions.EpubExtractor,
                        EmbeddedAuthorMetadataVersions.EpubLimits),
                    EmbeddedAuthorMetadataStatus.Available,
                    null,
                    [$"Resolved Writer {NumberWord((int)request.BookId.Value)}"]));
            });
        IPdfAuthorMetadataInspector pdfInspector = A.Fake<IPdfAuthorMetadataInspector>();
        A.CallTo(() => pdfInspector.InspectAuthorMetadataAsync(
                A<PdfInspectionRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                inspections++;
                pdfWorkerLaunches++;
                PdfInspectionRequest request = call.GetArgument<PdfInspectionRequest>(0)!;
                return Task.FromResult(new EmbeddedAuthorMetadataObservation(
                    new(
                        "PDF", request.Fingerprint,
                        EmbeddedAuthorMetadataVersions.PdfExtractor,
                        EmbeddedAuthorMetadataVersions.PdfLimits),
                    EmbeddedAuthorMetadataStatus.Available,
                    null,
                    [$"Resolved Writer {NumberWord((int)request.BookId.Value)}"]));
            });
        InspectEmbeddedAuthorMetadataUseCase useCase = new(
            resolver, inspector, pdfInspector, cache);

        Stopwatch stopwatch = Stopwatch.StartNew();
        FinalAuthorReviewWorkspace cold = await useCase.ExecuteAsync(
            state, PrepareFinalAuthorReviewUseCase.Execute(state), null, CancellationToken.None);
        long coldMilliseconds = stopwatch.ElapsedMilliseconds;
        inspections.Should().Be(suspectCount);
        cold.MetadataInspectionCount.Should().Be(suspectCount);
        cold.MetadataCacheHitCount.Should().Be(0);
        pdfWorkerLaunches.Should().Be(suspectCount / 2);

        inspections = 0;
        pdfWorkerLaunches = 0;
        stopwatch.Restart();
        FinalAuthorReviewWorkspace warm = await useCase.ExecuteAsync(
            state, PrepareFinalAuthorReviewUseCase.Execute(state), null, CancellationToken.None);
        long warmMilliseconds = stopwatch.ElapsedMilliseconds;
        inspections.Should().Be(0);
        warm.MetadataInspectionCount.Should().Be(0);
        warm.MetadataCacheHitCount.Should().Be(suspectCount);
        pdfWorkerLaunches.Should().Be(0);
        A.CallTo(() => resolver.ResolveFormat(
            A<ValidatedLibraryLocation>._, A<string>._, A<string>._, A<string>._))
            .MustHaveHappened(suspectCount, Times.Exactly);
        output.WriteLine(
            $"records={recordCount}; suspect={suspectCount}; coldMs={coldMilliseconds}; "
            + $"warmMs={warmMilliseconds}; coldInspections={suspectCount}; "
            + $"coldPdfWorkerLaunches={suspectCount / 2}; warmCacheHits={suspectCount}; "
            + "warmPdfWorkerLaunches=0");
    }

    private static FormatFileFingerprint Fingerprint(int value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));
        return new(100 + value, new(Convert.ToHexStringLower(digest)));
    }

    private static string NumberWord(int value) => $"Number{value}";
}
