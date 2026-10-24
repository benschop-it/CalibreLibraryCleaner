using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class PrepareMetadataReviewUseCaseTests
{
    [Fact]
    public async Task ExplicitLocalIdentityCanRepairATitleWithoutAnyAvailableOnlineProvider()
    {
        FormatFileFingerprint fingerprint = new(100, new(new string('b', 64)));
        CalibreBook book = new(
            new(1), "(2011 Broken Label", "Writer, Reliable",
            [new(new(1), "Reliable Writer", "Writer, Reliable")], [],
            [new("EPUB", "book", "Writer/Book (1)/book.epub", FormatFileStatus.Present, fingerprint,
                new(100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))], "Writer/Book (1)");
        LibraryStateGenerationId generation = new(Guid.Parse("cccccccc-bbbb-cccc-dddd-eeeeeeeeeeee"));
        LibraryState state = new(
            generation, new(0), LibraryStateStatus.Authoritative,
            new(new("generated", 1, "C:\\generated"), DateTimeOffset.UnixEpoch, [book], []),
            DateTimeOffset.UnixEpoch, workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed, generation, new(0), LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch, new(generation, new(0))));
        FrontMatterAuthorObservation local = new(
            "EPUB", fingerprint, FrontMatterAuthorVersions.EpubExtractor, FrontMatterAuthorVersions.Limits,
            FrontMatterAuthorStatus.Available,
            [new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 2, "By")], 1, 1, 100, false,
            identityCandidates: [new("A Northern Valley", ["Reliable Writer"], 1, 1, 2, true)]);
        FinalAuthorReviewWorkspace identity = PrepareFinalAuthorReviewUseCase.Execute(
            state, frontMatterObservations: [local]);
        PrepareMetadataReviewUseCase useCase = new(
            new([], A.Fake<IEditionMetadataProposalCache>(), A.Fake<IClock>(), new()),
            A.Fake<IMetadataReviewDecisionStore>());

        MetadataReviewWorkspace result = await useCase.ExecuteAsync(
            state, [], null, CancellationToken.None, identity);

        ReviewedMetadataSubject repair = result.Subjects.Should().ContainSingle().Subject;
        repair.Apply.Should().BeFalse();
        repair.Subject.Proposal.Confidence.Should().Be(FusedEditionMetadataConfidence.Low);
        repair.Subject.Proposal.Candidate!.Title.Should().Be("A Northern Valley");
        repair.Subject.Proposal.FieldSources.Should().Contain(value =>
            value.Field == EditionMetadataField.Title && value.Provider.Id == "local-front-matter");
        result.ProviderResults!.Providers.Sum(value => value.ProviderRequests).Should().Be(0);
    }

    [Fact]
    public async Task CompatibleOverrideRestoresAndStaleDecisionIsPruned()
    {
        Harness harness = Harness.Create();
        MetadataReviewWorkspace initial = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        MetadataReviewSubject subject = initial.Subjects.Single().Subject;
        MetadataReviewDecision compatible = MetadataReviewDecisionPolicy.CreateOverride(
            subject, harness.State.GenerationId, harness.State.Revision, apply: false)!;
        MetadataReviewDecision stale = new(
            new(
                subject.Id,
                subject.Proposal.PolicyVersion,
                new(subject.Proposal.PrimaryProvider!, "stale-edition"),
                harness.State.GenerationId,
                harness.State.Revision),
            false);
        A.CallTo(() => harness.Store.ReadAsync(A<string>._, A<CancellationToken>._))
            .Returns([compatible, stale]);

        MetadataReviewWorkspace restored = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);

        restored.Subjects.Single().Apply.Should().BeFalse();
        restored.Subjects.Single().IsOverride.Should().BeTrue();
        A.CallTo(() => harness.Store.WriteAsync(
                harness.State.Snapshot.Identity.LibraryRoot,
                A<IReadOnlyList<MetadataReviewDecision>>.That.Matches(value =>
                    value.Count == 1 && value[0] == compatible),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ApplyOverridePersistsAndReturningToDefaultRemovesDecision()
    {
        Harness harness = Harness.Create();
        MetadataReviewWorkspace workspace = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        MetadataReviewSubjectId subjectId = workspace.Subjects.Single().Subject.Id;

        MetadataReviewWorkspace overridden = await harness.UseCase.SetApplyAsync(
            workspace, subjectId, apply: false, CancellationToken.None);
        MetadataReviewWorkspace reset = await harness.UseCase.SetApplyAsync(
            overridden, subjectId, apply: true, CancellationToken.None);

        overridden.Subjects.Single().IsOverride.Should().BeTrue();
        overridden.Subjects.Single().Apply.Should().BeFalse();
        reset.Subjects.Single().IsOverride.Should().BeFalse();
        reset.Subjects.Single().Apply.Should().BeTrue();
        A.CallTo(() => harness.Store.WriteAsync(
                A<string>._,
                A<IReadOnlyList<MetadataReviewDecision>>.That.Matches(value => value.Count == 0),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task BulkClearPersistsAllDefaultSelectionsAsUncheckedInOneWrite()
    {
        Harness harness = Harness.Create();
        MetadataReviewWorkspace workspace = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        Fake.ClearRecordedCalls(harness.Store);

        MetadataReviewWorkspace cleared = await harness.UseCase.SetAllApplyAsync(
            workspace, apply: false, CancellationToken.None);

        cleared.Subjects.Should().OnlyContain(value => !value.Apply && value.IsOverride);
        A.CallTo(() => harness.Store.WriteAsync(
                cleared.LibraryRoot,
                A<IReadOnlyList<MetadataReviewDecision>>.That.Matches(value =>
                    value.Count == cleared.Subjects.Count && value.All(decision => !decision.Apply)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BulkSelectedUpdateChangesOnlySelectedSubjectsInOneWrite()
    {
        Harness harness = Harness.Create();
        MetadataReviewWorkspace initial = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        ReviewedMetadataSubject first = initial.Subjects.Single();
        MetadataReviewSubject secondSubject = new(
            new("second-subject"),
            first.Subject.UnifiedGroupId,
            first.Subject.Members,
            first.Subject.TargetBookId,
            first.Subject.Proposal);
        MetadataReviewWorkspace workspace = new(
            initial.LibraryRoot,
            initial.GenerationId,
            initial.Revision,
            [first, new(secondSubject, true, false, null)],
            true);
        Fake.ClearRecordedCalls(harness.Store);

        MetadataReviewWorkspace updated = await harness.UseCase.SetApplyAsync(
            workspace,
            [secondSubject.Id],
            apply: false,
            CancellationToken.None);

        updated.Subjects.Single(value => value.Subject.Id == first.Subject.Id).Apply.Should().BeTrue();
        updated.Subjects.Single(value => value.Subject.Id == secondSubject.Id).Apply.Should().BeFalse();
        A.CallTo(() => harness.Store.WriteAsync(
                workspace.LibraryRoot,
                A<IReadOnlyList<MetadataReviewDecision>>.That.Matches(value =>
                    value.Count == 1 && value[0].Key.SubjectId == secondSubject.Id && !value[0].Apply),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PersistenceFailureKeepsSessionReviewAndDoesNotThrow()
    {
        Harness harness = Harness.Create();
        A.CallTo(() => harness.Store.ReadAsync(A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new IOException("private"));

        MetadataReviewWorkspace workspace = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);

        workspace.PersistenceAvailable.Should().BeFalse();
        workspace.PersistenceProblemCode.Should().Be("METADATA_REVIEW.DECISION_READ_FAILED");
        workspace.Subjects.Should().ContainSingle();
        workspace.Subjects.Single().Apply.Should().BeTrue();
    }

    [Fact]
    public async Task AlreadyAppliedProposalIsUncheckedAndStaleOverrideIsPruned()
    {
        Harness harness = Harness.Create(alreadyApplied: true);
        MetadataReviewWorkspace initial = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        MetadataReviewSubject subject = initial.Subjects.Single().Subject;
        MetadataReviewDecision staleApply = new(
            MetadataReviewDecisionPolicy.CreateKey(
                subject, harness.State.GenerationId, harness.State.Revision),
            true);
        A.CallTo(() => harness.Store.ReadAsync(A<string>._, A<CancellationToken>._))
            .Returns([staleApply]);

        MetadataReviewWorkspace converged = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);

        ReviewedMetadataSubject reviewed = converged.Subjects.Single();
        reviewed.IsAlreadyApplied.Should().BeTrue();
        reviewed.Apply.Should().BeFalse();
        reviewed.IsOverride.Should().BeFalse();
        A.CallTo(() => harness.Store.WriteAsync(
                converged.LibraryRoot,
                A<IReadOnlyList<MetadataReviewDecision>>.That.IsEmpty(),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task RiskyMediumProposalDefaultsToNeedsReviewAndExplicitApplyPersists()
    {
        Harness harness = Harness.Create(riskyProposal: true);

        MetadataReviewWorkspace workspace = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);

        ReviewedMetadataSubject reviewed = workspace.Subjects.Single();
        reviewed.Subject.Proposal.Confidence.Should().Be(FusedEditionMetadataConfidence.Medium);
        reviewed.Apply.Should().BeFalse();
        reviewed.RequiresReview.Should().BeTrue();
        reviewed.IsOverride.Should().BeFalse();

        MetadataReviewWorkspace applied = await harness.UseCase.SetApplyAsync(
            workspace, reviewed.Subject.Id, true, CancellationToken.None);
        ReviewedMetadataSubject explicitApply = applied.Subjects.Single();
        explicitApply.Apply.Should().BeTrue();
        explicitApply.IsOverride.Should().BeTrue();
        explicitApply.RequiresReview.Should().BeTrue();
        A.CallTo(() => harness.Store.WriteAsync(
                applied.LibraryRoot,
                A<IReadOnlyList<MetadataReviewDecision>>.That.Matches(value =>
                    value.Count == 1 && value[0].Apply),
                A<CancellationToken>._))
            .MustHaveHappened();

        MetadataReviewWorkspace reset = await harness.UseCase.SetApplyAsync(
            applied, reviewed.Subject.Id, false, CancellationToken.None);
        reset.Subjects.Single().Apply.Should().BeFalse();
        reset.Subjects.Single().RequiresReview.Should().BeTrue();
        reset.Subjects.Single().IsOverride.Should().BeFalse();
    }

    private sealed record Harness(
        PrepareMetadataReviewUseCase UseCase,
        IMetadataReviewDecisionStore Store,
        LibraryState State)
    {
        public static Harness Create(bool alreadyApplied = false, bool riskyProposal = false)
        {
            EditionMetadataProviderIdentity providerIdentity = new("provider", "provider/1.0.0");
            IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
            A.CallTo(() => provider.Identity).Returns(providerIdentity);
            A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    EditionMetadataLookupQuery query = call.GetArgument<EditionMetadataLookupQuery>(0)!;
                    return Task.FromResult(new EditionMetadataSearchResult(
                        providerIdentity,
                        EditionMetadataSearchStatus.Success,
                        DateTimeOffset.UnixEpoch,
                        [riskyProposal
                            ? new(
                                "work", "edition", "TITLE", ["Author"],
                                [new EditionMetadataIdentifier("isbn", "9780306406157")],
                                "Different Publisher", new(1994))
                            : new(
                                "work", "edition", "Title", ["Author"],
                                [new EditionMetadataIdentifier("isbn", "9780306406157")],
                                "Provider Publisher") ]));
                });
            IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(DateTimeOffset.UnixEpoch);
            ResolveEditionMetadataProposalsUseCase resolver = new([provider], cache, clock, new());
            IMetadataReviewDecisionStore store = A.Fake<IMetadataReviewDecisionStore>();
            A.CallTo(() => store.ReadAsync(A<string>._, A<CancellationToken>._))
                .Returns(Array.Empty<MetadataReviewDecision>());
            PrepareMetadataReviewUseCase useCase = new(resolver, store);
            CalibreBook book = new(
                new(1), "Title", "Author", [new(new(1), "Author", "Author")],
                [new("isbn", "9780306406157")], [], "Author/Title (1)",
                new(
                    publisher: riskyProposal ? "Current Publisher" : alreadyApplied ? "Provider Publisher" : null,
                    publicationDate: riskyProposal
                        ? new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        : null));
            LibrarySnapshot snapshot = new(
                new("library", 27, "C:\\library"), DateTimeOffset.UnixEpoch, [book], []);
            LibraryStateGenerationId generation = new(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            LibraryState state = new(
                generation,
                new(0),
                LibraryStateStatus.Authoritative,
                snapshot,
                DateTimeOffset.UnixEpoch,
                workflowCheckpoint: new(
                    LibraryWorkflowPhase.CandidateCleanupCompleted,
                    generation,
                    new(0),
                    LibraryWorkflowPolicyVersions.Current,
                    DateTimeOffset.UnixEpoch,
                    new(generation, new(0))));
            return new(useCase, store, state);
        }
    }
}
