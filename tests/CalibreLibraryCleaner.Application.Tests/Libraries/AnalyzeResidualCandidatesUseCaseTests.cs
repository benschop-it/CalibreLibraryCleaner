using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class AnalyzeResidualCandidatesUseCaseTests
{
    private const string Root = "C:\\library";
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactMetadataCandidatesSurviveExpandedLimitAndPublishCandidateAnalysis()
    {
        CalibreBook[] books = Enumerable.Range(1, 25).Select(Book).ToArray();
        TestContext context = Context(books);
        A.CallTo(() => context.Discoverer.ExecuteAsync(
                A<IReadOnlyList<CalibreBook>>._,
                A<IReadOnlyList<Domain.Assessments.EpubAssessment>>._,
                A<IReadOnlyList<EpubAssessmentTarget>>._,
                A<int>._,
                A<IProgress<WorkLanguageDiscoveryProgress>?>._,
                A<CancellationToken>._))
            .Returns(new WorkLanguageDiscoveryResult(
                [],
                new(
                    MatchingPolicyVersion.Current,
                    MatchingEvidenceStatus.Unavailable,
                    books.Length,
                    10_000,
                    0,
                    books.Length,
                    0,
                    0,
                    0,
                    0,
                    0),
                LimitExceeded: true));

        ResidualCandidateAnalysisResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ExpandedLimitExceeded.Should().BeTrue();
        result.ExactMetadataGroupCount.Should().Be(1);
        result.UnifiedGroupCount.Should().Be(1);
        result.State!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidateAnalysisReady);
        result.State.Snapshot.UnifiedCandidateGroups.Single().Members.Should().HaveCount(25);
        result.State.Snapshot.Findings.Should().Contain(value =>
            value.Code == "MATCHING.CANDIDATE_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task DiscoveryReceivesCurrentPhysicalEpubTargets()
    {
        CalibreBook[] books = [Book(1), Book(2)];
        TestContext context = Context(books);
        IReadOnlyList<EpubAssessmentTarget>? captured = null;
        A.CallTo(() => context.Discoverer.ExecuteAsync(
                A<IReadOnlyList<CalibreBook>>._,
                A<IReadOnlyList<Domain.Assessments.EpubAssessment>>._,
                A<IReadOnlyList<EpubAssessmentTarget>>._,
                A<int>._,
                A<IProgress<WorkLanguageDiscoveryProgress>?>._,
                A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<IReadOnlyList<EpubAssessmentTarget>>(2))
            .Returns(Discovery(books.Length));

        ResidualCandidateAnalysisResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured.Should().HaveCount(2).And.OnlyContain(value =>
            value.FileStatus == FormatFileStatus.Present
            && value.Fingerprint != null
            && value.Observation != null
            && value.LibraryRoot == Root
            && !string.IsNullOrWhiteSpace(value.FullPath));
    }

    [Fact]
    public async Task PreservedInvalidEpubIsExcludedFromContentTargetsButRetainsMetadataEvidence()
    {
        CalibreBook valid = Book(1);
        CalibreBook source = Book(2);
        CalibreBook invalid = new(
            source.Id,
            source.Title,
            source.AuthorSort,
            source.Authors,
            source.Identifiers,
            [new BookFormat("EPUB", "book", string.Empty, FormatFileStatus.InvalidPath)],
            source.RelativeDirectory,
            source.PublicationMetadata);
        CalibreBook[] books = [valid, invalid];
        TestContext context = Context(books);
        IReadOnlyList<EpubAssessmentTarget>? captured = null;
        A.CallTo(() => context.Discoverer.ExecuteAsync(
                A<IReadOnlyList<CalibreBook>>._,
                A<IReadOnlyList<Domain.Assessments.EpubAssessment>>._,
                A<IReadOnlyList<EpubAssessmentTarget>>._,
                A<int>._,
                A<IProgress<WorkLanguageDiscoveryProgress>?>._,
                A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<IReadOnlyList<EpubAssessmentTarget>>(2))
            .Returns(Discovery(books.Length));

        ResidualCandidateAnalysisResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ExactMetadataGroupCount.Should().Be(1);
        captured.Should().ContainSingle(value => value.BookId == valid.Id);
    }

    [Fact]
    public async Task CancellationDuringDiscoveryLeavesRefreshedGenerationAuthoritative()
    {
        CalibreBook[] books = [Book(1), Book(2)];
        TestContext context = Context(books);
        A.CallTo(() => context.Discoverer.ExecuteAsync(
                A<IReadOnlyList<CalibreBook>>._,
                A<IReadOnlyList<Domain.Assessments.EpubAssessment>>._,
                A<IReadOnlyList<EpubAssessmentTarget>>._,
                A<int>._,
                A<IProgress<WorkLanguageDiscoveryProgress>?>._,
                A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> action = () => context.UseCase.ExecuteAsync(Root, null, CancellationToken.None);

        await action.Should().ThrowAsync<OperationCanceledException>();
        A.CallTo(() => context.State.CompleteCandidateAnalysisAsync(
            A<LibrarySnapshot>._,
            A<LibraryStateGenerationId>._,
            A<LibraryStateRevision>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        context.State.GetCurrent(Root)!.WorkflowCheckpoint.Phase.Should()
            .Be(LibraryWorkflowPhase.CandidatePreparationReady);
    }

    [Fact]
    public async Task StalePublicationReturnsFailureWithoutCandidateState()
    {
        CalibreBook[] books = [Book(1), Book(2)];
        TestContext context = Context(books);
        A.CallTo(() => context.Discoverer.ExecuteAsync(
                A<IReadOnlyList<CalibreBook>>._,
                A<IReadOnlyList<Domain.Assessments.EpubAssessment>>._,
                A<IReadOnlyList<EpubAssessmentTarget>>._,
                A<int>._,
                A<IProgress<WorkLanguageDiscoveryProgress>?>._,
                A<CancellationToken>._))
            .Returns(Discovery(books.Length));
        A.CallTo(() => context.State.CompleteCandidateAnalysisAsync(
                A<LibrarySnapshot>._,
                A<LibraryStateGenerationId>._,
                A<LibraryStateRevision>._,
                A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Failure(
                "LIBRARY_STATE.CANDIDATE_SOURCE_STALE", "Source changed."));

        ResidualCandidateAnalysisResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.ErrorCode.Should().Be("CANDIDATE_ANALYSIS.PUBLICATION_FAILED");
        result.State.Should().BeNull();
    }

    private static TestContext Context(CalibreBook[] books)
    {
        ILibraryStateSession state = A.Fake<ILibraryStateSession>();
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        IWorkLanguageCandidateDiscoverer discoverer = A.Fake<IWorkLanguageCandidateDiscoverer>();
        IClock clock = A.Fake<IClock>();
        LibraryStateGenerationId generation = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        LibraryWorkflowSource source = new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), new(3));
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, Root),
            Now,
            books,
            []);
        LibraryState refreshed = new(
            generation,
            new(0),
            LibraryStateStatus.Authoritative,
            snapshot,
            Now,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.CandidatePreparationReady,
                generation,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                Now,
                source));
        ValidatedLibraryLocation location = new(Root, $"{Root}\\metadata.db");
        A.CallTo(() => state.GetCurrent(Root)).Returns(refreshed);
        A.CallTo(() => resolver.ValidateAsync(Root, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                "EPUB"))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                string storedName = call.GetArgument<string>(2)!;
                string relative = $"{directory}/{storedName}.epub";
                return ResolvedFormatPathOutcome.Success(new(
                    Root, $"{Root}\\{relative.Replace('/', '\\')}", relative));
            });
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(1));
        A.CallTo(() => state.CompleteCandidateAnalysisAsync(
                A<LibrarySnapshot>._,
                generation,
                new(0),
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                LibrarySnapshot analyzed = call.GetArgument<LibrarySnapshot>(0)!;
                return Task.FromResult(LibraryStateSessionOutcome.Success(new(
                    generation,
                    new(0),
                    LibraryStateStatus.Authoritative,
                    analyzed,
                    analyzed.ScannedAt,
                    workflowCheckpoint: new(
                        LibraryWorkflowPhase.CandidateAnalysisReady,
                        generation,
                        new(0),
                        LibraryWorkflowPolicyVersions.Current,
                        analyzed.ScannedAt,
                        source))));
            });
        return new(
            state,
            discoverer,
            new(state, resolver, discoverer, clock, new()));
    }

    private static WorkLanguageDiscoveryResult Discovery(int recordCount) => new(
        [],
        new(
            MatchingPolicyVersion.Current,
            MatchingEvidenceStatus.Available,
            recordCount,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0),
        LimitExceeded: false);

    private static CalibreBook Book(int id)
    {
        FormatFileFingerprint fingerprint = new(100 + id, new(new string('a', 64)));
        return new(
            new(id),
            "Shared Book",
            "Author",
            [new(new(id), "Author", "Author")],
            [],
            [new(
                "EPUB",
                "book",
                $"Author/Book {id}/book.epub",
                FormatFileStatus.Present,
                fingerprint,
                new(fingerprint.SizeInBytes, Now, Now, 0))],
            $"Author/Book {id}",
            new(languages: ["eng"]));
    }

    private sealed record TestContext(
        ILibraryStateSession State,
        IWorkLanguageCandidateDiscoverer Discoverer,
        AnalyzeResidualCandidatesUseCase UseCase);
}
