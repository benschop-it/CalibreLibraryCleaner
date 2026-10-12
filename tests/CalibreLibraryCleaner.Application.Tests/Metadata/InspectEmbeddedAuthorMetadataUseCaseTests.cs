using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class InspectEmbeddedAuthorMetadataUseCaseTests
{
    private const string Root = "C:\\library";
    private static readonly FormatFileFingerprint Fingerprint = new(
        10, new(new string('a', 64)));
    private static readonly FormatFileObservation Observation = new(
        10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);

    [Fact]
    public async Task CleanBooksOpenNoFormats()
    {
        Harness harness = Harness.Create(Book("Reliable Writer", includeEpub: false));
        FinalAuthorReviewWorkspace initial = PrepareFinalAuthorReviewUseCase.Execute(harness.State);

        FinalAuthorReviewWorkspace result = await harness.UseCase.ExecuteAsync(
            harness.State, initial, null, CancellationToken.None);

        result.Subjects.Should().BeEmpty();
        A.CallTo(() => harness.Cache.TryReadAsync(
            A<EmbeddedAuthorMetadataCacheKey>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Inspector.InspectAuthorMetadataAsync(
            A<EpubInspectionRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Resolver.ResolveFormat(
            A<ValidatedLibraryLocation>._, A<string>._, A<string>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CacheHitAvoidsPathResolutionAndInspection()
    {
        Harness harness = Harness.Create(Book("Unknown", includeEpub: true));
        A.CallTo(() => harness.Cache.TryReadAsync(
                A<EmbeddedAuthorMetadataCacheKey>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<EmbeddedAuthorMetadataObservation?>(new(
                call.GetArgument<EmbeddedAuthorMetadataCacheKey>(0)!,
                EmbeddedAuthorMetadataStatus.Available,
                "The Book",
                ["Reliable Writer"])));

        FinalAuthorReviewWorkspace result = await harness.UseCase.ExecuteAsync(
            harness.State,
            PrepareFinalAuthorReviewUseCase.Execute(harness.State),
            null,
            CancellationToken.None);

        result.MetadataCacheHitCount.Should().Be(1);
        result.MetadataInspectionCount.Should().Be(0);
        result.Subjects.Single().Resolution.ProposalSource
            .Should().Be(AuthorProposalSource.CachedEmbeddedMetadata);
        A.CallTo(() => harness.Inspector.InspectAuthorMetadataAsync(
            A<EpubInspectionRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Resolver.ResolveFormat(
            A<ValidatedLibraryLocation>._, A<string>._, A<string>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CacheMissInspectsAndPersistsOneDemandedEpub()
    {
        Harness harness = Harness.Create(Book("Unknown", includeEpub: true));
        A.CallTo(() => harness.Inspector.InspectAuthorMetadataAsync(
                A<EpubInspectionRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                return Task.FromResult(new EmbeddedAuthorMetadataObservation(
                    new(
                        "EPUB", request.Fingerprint,
                        EmbeddedAuthorMetadataVersions.EpubExtractor,
                        EmbeddedAuthorMetadataVersions.EpubLimits),
                    EmbeddedAuthorMetadataStatus.Available,
                    "The Book",
                        ["Reliable Writer"]));
            });

        FinalAuthorReviewWorkspace result = await harness.UseCase.ExecuteAsync(
            harness.State,
            PrepareFinalAuthorReviewUseCase.Execute(harness.State),
            null,
            CancellationToken.None);

        result.MetadataCacheHitCount.Should().Be(0);
        result.MetadataInspectionCount.Should().Be(1);
        A.CallTo(() => harness.Cache.WriteAsync(
            A<EmbeddedAuthorMetadataObservation>.That.Matches(value =>
                value.Status == EmbeddedAuthorMetadataStatus.Available),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PdfCacheMissUsesOnlyTheIsolatedPdfMetadataPort()
    {
        Harness harness = Harness.Create(Book("Unknown", includeEpub: false, includePdf: true));
        A.CallTo(() => harness.PdfInspector.InspectAuthorMetadataAsync(
                A<PdfInspectionRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                PdfInspectionRequest request = call.GetArgument<PdfInspectionRequest>(0)!;
                return Task.FromResult(new EmbeddedAuthorMetadataObservation(
                    new(
                        "PDF", request.Fingerprint,
                        EmbeddedAuthorMetadataVersions.PdfExtractor,
                        EmbeddedAuthorMetadataVersions.PdfLimits),
                    EmbeddedAuthorMetadataStatus.Available,
                    "The Book",
                    ["PDF Writer"]));
            });

        FinalAuthorReviewWorkspace result = await harness.UseCase.ExecuteAsync(
            harness.State,
            PrepareFinalAuthorReviewUseCase.Execute(harness.State),
            null,
            CancellationToken.None);

        result.MetadataInspectionCount.Should().Be(1);
        result.Subjects.Single().Resolution.ProposedDisplayNames.Should().Equal("PDF Writer");
        A.CallTo(() => harness.Inspector.InspectAuthorMetadataAsync(
            A<EpubInspectionRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    private sealed record Harness(
        InspectEmbeddedAuthorMetadataUseCase UseCase,
        ILibraryPathResolver Resolver,
        IEpubInspector Inspector,
        IPdfAuthorMetadataInspector PdfInspector,
        IEmbeddedAuthorMetadataCache Cache,
        LibraryState State)
    {
        public static Harness Create(CalibreBook book)
        {
            ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
            A.CallTo(() => resolver.ValidateAsync(Root, A<CancellationToken>._))
                .Returns(LibraryValidationOutcome.Success(new(Root, $"{Root}\\metadata.db")));
            A.CallTo(() => resolver.ResolveFormat(
                    A<ValidatedLibraryLocation>._, A<string>._, A<string>._, A<string>._))
                .ReturnsLazily(call =>
                {
                    string format = call.GetArgument<string>(3)!;
                    string extension = format.ToLowerInvariant();
                    return ResolvedFormatPathOutcome.Success(new(
                        Root,
                        $"{Root}\\Author\\The Book (1)\\book.{extension}",
                        $"Author/The Book (1)/book.{extension}"));
                });
            IEpubInspector inspector = A.Fake<IEpubInspector>();
            IPdfAuthorMetadataInspector pdfInspector = A.Fake<IPdfAuthorMetadataInspector>();
            IEmbeddedAuthorMetadataCache cache = A.Fake<IEmbeddedAuthorMetadataCache>();
            LibrarySnapshot snapshot = new(
                new("library", 27, Root), DateTimeOffset.UnixEpoch, [book], []);
            LibraryStateGenerationId generation = new(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            LibraryState state = new(
                generation,
                new(0),
                LibraryStateStatus.Authoritative,
                snapshot,
                DateTimeOffset.UnixEpoch,
                workflowCheckpoint: new(
                    LibraryWorkflowPhase.Completed,
                    generation,
                    new(0),
                    LibraryWorkflowPolicyVersions.Current,
                    DateTimeOffset.UnixEpoch,
                    new(generation, new(0))));
            return new(
                new(resolver, inspector, pdfInspector, cache), resolver, inspector, pdfInspector,
                cache, state);
        }
    }

    private static CalibreBook Book(
        string author,
        bool includeEpub,
        bool includePdf = false)
    {
        List<BookFormat> formats = [];
        if (includeEpub)
            formats.Add(new(
                "EPUB", "book", "Author/The Book (1)/book.epub",
                FormatFileStatus.Present, Fingerprint, Observation));
        if (includePdf)
            formats.Add(new(
                "PDF", "book", "Author/The Book (1)/book.pdf",
                FormatFileStatus.Present, Fingerprint, Observation));
        return new(
            new(1), "The Book", author, [new(new(1), author, author)], [], formats,
            "Author/The Book (1)");
    }
}
