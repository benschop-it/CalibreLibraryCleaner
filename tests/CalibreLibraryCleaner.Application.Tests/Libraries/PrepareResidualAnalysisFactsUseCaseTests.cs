using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class PrepareResidualAnalysisFactsUseCaseTests
{
    private const string Root = "C:\\library";
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly FormatFileFingerprint EpubFingerprint = Fingerprint('a', 10);
    private static readonly FormatFileFingerprint PdfFingerprint = Fingerprint('b', 20);

    [Fact]
    public async Task CompatibleEpubAndPdfFactsRebindWithoutInspection()
    {
        EpubAssessment epub = new EpubAssessmentEngine().Assess(
            new(1),
            "old.epub",
            EpubFingerprint,
            EpubInspectionResult.Failed(
                new(1), "old.epub", EpubInspectionProblemCode.CannotOpen, "Synthetic."));
        PdfAssessment pdf = new PdfAssessmentEngine(new()).Assess(
            new(1),
            "old.pdf",
            PdfFingerprint,
            PdfInspectionResult.Failed(new(1), "old.pdf", PdfInspectionProblemCode.MalformedStructure),
            Observation(PdfFingerprint));
        LibrarySnapshot reusable = Snapshot([epub], [pdf]);
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        A.CallTo(() => store.ReadReusableAssessmentSnapshotAsync(Root, A<CancellationToken>._))
            .Returns(reusable);
        IEpubInspector epubInspector = A.Fake<IEpubInspector>();
        IPdfInspector pdfInspector = A.Fake<IPdfInspector>();
        PrepareResidualAnalysisFactsUseCase useCase = UseCase(store, epubInspector, pdfInspector,
            A.Fake<IEpubContentSignatureInspector>(), A.Fake<IEpubContentSignatureCache>());
        EpubAssessmentTarget epubTarget = new(
            new(2), "EPUB", "new.epub", Root, $"{Root}\\new.epub", FormatFileStatus.Present,
            EpubFingerprint, Observation(EpubFingerprint));
        PdfAssessmentTarget pdfTarget = new(
            new(2), "PDF", "new.pdf", Root, $"{Root}\\new.pdf", FormatFileStatus.Present,
            PdfFingerprint, Observation(PdfFingerprint));

        ResidualAnalysisFacts facts = await useCase.PrepareAsync(
            Root, [epubTarget], [pdfTarget], null, CancellationToken.None);

        facts.ReusedEpubAssessmentCount.Should().Be(1);
        facts.ReusedPdfAssessmentCount.Should().Be(1);
        facts.FreshEpubAssessmentCount.Should().Be(0);
        facts.FreshPdfAssessmentCount.Should().Be(0);
        facts.EpubAssessments.Should().ContainSingle(value =>
            value.CalibreBookId == new CalibreBookId(2)
            && value.ExpectedRelativePath == "new.epub");
        facts.PdfAssessments.Should().ContainSingle(value =>
            value.CalibreBookId == new CalibreBookId(2)
            && value.ExpectedRelativePath == "new.pdf"
            && value.ObservedObservation == pdfTarget.Observation);
        A.CallTo(() => epubInspector.InspectAsync(
            A<EpubInspectionRequest>._,
            A<IProgress<EpubInspectionProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => pdfInspector.InspectAsync(
            A<PdfInspectionRequest>._,
            A<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>._,
            A<IProgress<PdfInspectionProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ContentCacheHitsBindCurrentRecordsWithoutInspection()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        IEpubContentSignatureInspector inspector = A.Fake<IEpubContentSignatureInspector>();
        IEpubContentSignatureCache cache = A.Fake<IEpubContentSignatureCache>();
        A.CallTo(() => cache.TryReadAsync(
                A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<EpubContentSignature?>(
                Signature(call.GetArgument<EpubContentSignatureCacheKey>(0)!.Fingerprint)));
        PrepareResidualAnalysisFactsUseCase useCase = UseCase(
            store, A.Fake<IEpubInspector>(), A.Fake<IPdfInspector>(), inspector, cache);
        EpubAssessmentTarget first = Target(10, 'a');
        EpubAssessmentTarget second = Target(20, 'b');
        ResidualAnalysisFacts facts = new([first, second], [], [], [], 0, 0, 0, 0);
        BookCandidatePair pair = new(
            new(new(10), new(20)),
            500,
            [new("MATCH.TITLE.SIMILAR", CandidateEvidenceStrength.Supporting)],
            [],
            needsContentEvidence: true);

        CandidateContentSignatureBatchResult result = await useCase.ResolveCandidateContentSignaturesAsync(
            facts, [pair], null, CancellationToken.None);

        result.CacheHits.Should().Be(2);
        result.Inspections.Should().Be(0);
        result.Signatures.Keys.Should().BeEquivalentTo([new CalibreBookId(10), new(20)]);
        A.CallTo(() => inspector.InspectContentSignatureAsync(
            A<EpubContentSignatureRequest>._,
            A<IProgress<EpubContentSignatureProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MissingAssessmentFactsInspectOnlyFreshTargets()
    {
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        A.CallTo(() => store.ReadReusableAssessmentSnapshotAsync(Root, A<CancellationToken>._))
            .Returns(Task.FromResult<LibrarySnapshot?>(null));
        IEpubInspector epubInspector = A.Fake<IEpubInspector>();
        A.CallTo(() => epubInspector.InspectAsync(
                A<EpubInspectionRequest>._,
                A<IProgress<EpubInspectionProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                return Task.FromResult(EpubInspectionResult.Failed(
                    request.BookId,
                    request.ExpectedRelativePath,
                    EpubInspectionProblemCode.CannotOpen,
                    "Synthetic."));
            });
        IPdfInspector pdfInspector = A.Fake<IPdfInspector>();
        A.CallTo(() => pdfInspector.InspectAsync(
                A<PdfInspectionRequest>._,
                A<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>._,
                A<IProgress<PdfInspectionProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                PdfInspectionRequest request = call.GetArgument<PdfInspectionRequest>(0)!;
                return Task.FromResult(PdfInspectionResult.Failed(
                    request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.MalformedStructure));
            });
        PrepareResidualAnalysisFactsUseCase useCase = UseCase(
            store, epubInspector, pdfInspector,
            A.Fake<IEpubContentSignatureInspector>(), A.Fake<IEpubContentSignatureCache>());
        List<ResidualAnalysisFactsProgress> progress = [];

        ResidualAnalysisFacts facts = await useCase.PrepareAsync(
            Root,
            [Target(10, 'a')],
            [new(
                new(20), "PDF", "book-20.pdf", Root, $"{Root}\\book-20.pdf",
                FormatFileStatus.Present, PdfFingerprint, Observation(PdfFingerprint))],
            new InlineProgress<ResidualAnalysisFactsProgress>(progress.Add),
            CancellationToken.None);

        facts.FreshEpubAssessmentCount.Should().Be(1);
        facts.FreshPdfAssessmentCount.Should().Be(1);
        facts.ReusedEpubAssessmentCount.Should().Be(0);
        facts.ReusedPdfAssessmentCount.Should().Be(0);
        progress.Select(value => value.Phase).Should().ContainInOrder(
            ResidualAnalysisFactsPhase.AssessingEpubFormats,
            ResidualAnalysisFactsPhase.AssessingPdfFormats);
        progress.Should().Contain(value =>
            value.Phase == ResidualAnalysisFactsPhase.AssessingEpubFormats
            && value.CompletedFiles == 1
            && value.TotalFiles == 1);
        progress.Should().Contain(value =>
            value.Phase == ResidualAnalysisFactsPhase.AssessingPdfFormats
            && value.CompletedFiles == 1
            && value.TotalFiles == 1);
        A.CallTo(() => epubInspector.InspectAsync(
            A<EpubInspectionRequest>._,
            A<IProgress<EpubInspectionProgress>?>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => pdfInspector.InspectAsync(
            A<PdfInspectionRequest>._,
            A<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>._,
            A<IProgress<PdfInspectionProgress>?>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    private static PrepareResidualAnalysisFactsUseCase UseCase(
        ILibraryStateStore store,
        IEpubInspector epubInspector,
        IPdfInspector pdfInspector,
        IEpubContentSignatureInspector contentInspector,
        IEpubContentSignatureCache cache) => new(
        store,
        new(epubInspector, new()),
        new(pdfInspector, new(), new(new())),
        new(contentInspector, cache),
        new());

    private static LibrarySnapshot Snapshot(
        IEnumerable<EpubAssessment> epub,
        IEnumerable<PdfAssessment> pdf) => new(
        new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, Root),
        Now,
        [new(new(1), "Book", "Author", [new(new(1), "Author", "Author")], [], [], "Author/Book")],
        [],
        epubAssessments: epub,
        pdfAssessments: pdf);

    private static EpubAssessmentTarget Target(long id, char fingerprint) => new(
        new(id),
        "EPUB",
        $"book-{id}.epub",
        Root,
        $"{Root}\\book-{id}.epub",
        FormatFileStatus.Present,
        Fingerprint(fingerprint, 10),
        Observation(Fingerprint(fingerprint, 10)));

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
            new(new string('d', 64)),
            new(new string('e', 64)))));

    private static FormatFileFingerprint Fingerprint(char value, long size) => new(
        size,
        new(new string(value, 64)));

    private static FormatFileObservation Observation(FormatFileFingerprint fingerprint) => new(
        fingerprint.SizeInBytes,
        Now,
        Now,
        0);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
