using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class InspectFrontMatterAuthorsUseCaseTests
{
    [Fact]
    public async Task CacheHitAvoidsPathsAndInspectorsAndProducesUncheckedProposal()
    {
        const string root = "C:\\library";
        FormatFileFingerprint fingerprint = new(10, new(new string('a', 64)));
        FormatFileObservation fileObservation = new(
            10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);
        CalibreBook book = new(
            new(1),
            "The Book",
            "Unknown",
            [new(new(1), "Unknown", "Unknown")],
            [],
            [new(
                "EPUB", "book", "Author/The Book (1)/book.epub",
                FormatFileStatus.Present, fingerprint, fileObservation)],
            "Author/The Book (1)");
        LibraryStateGenerationId generation = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        LibraryState state = new(
            generation,
            new(0),
            LibraryStateStatus.Authoritative,
            new(new("library", 27, root), DateTimeOffset.UnixEpoch, [book], []),
            DateTimeOffset.UnixEpoch,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed,
                generation,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch,
                new(generation, new(0))));
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        A.CallTo(() => resolver.ValidateAsync(root, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(new(root, $"{root}\\metadata.db")));
        IEpubFrontMatterAuthorInspector epub = A.Fake<IEpubFrontMatterAuthorInspector>();
        IPdfFrontMatterAuthorInspector pdf = A.Fake<IPdfFrontMatterAuthorInspector>();
        IFrontMatterAuthorCache cache = A.Fake<IFrontMatterAuthorCache>();
        A.CallTo(() => cache.TryReadAsync(
                A<FrontMatterAuthorCacheKey>._, A<CancellationToken>._))
            .Returns(new FrontMatterAuthorObservation(
                "EPUB",
                fingerprint,
                FrontMatterAuthorVersions.EpubExtractor,
                FrontMatterAuthorVersions.Limits,
                FrontMatterAuthorStatus.Available,
                [new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 1, "By")],
                3,
                3,
                100,
                false));
        InspectFrontMatterAuthorsUseCase useCase = new(resolver, epub, pdf, cache);

        FinalAuthorReviewWorkspace result = await useCase.ExecuteAsync(
            state,
            PrepareFinalAuthorReviewUseCase.Execute(state),
            null,
            CancellationToken.None);

        result.FrontMatterCacheHitCount.Should().Be(1);
        result.FrontMatterInspectionCount.Should().Be(0);
        result.Subjects.Single().Resolution.ProposalSource
            .Should().Be(AuthorProposalSource.ContentDerivedFrontMatter);
        result.Subjects.Single().Resolution.SelectedByDefault.Should().BeFalse();
        A.CallTo(() => resolver.ResolveFormat(
            A<ValidatedLibraryLocation>._, A<string>._, A<string>._, A<string>._)).MustNotHaveHappened();
        A.CallTo(() => epub.InspectFrontMatterAuthorsAsync(
            A<EpubInspectionRequest>._, A<FrontMatterInspectionLimits>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }
}
