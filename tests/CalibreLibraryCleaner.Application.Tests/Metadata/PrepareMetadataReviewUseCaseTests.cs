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

    private sealed record Harness(
        PrepareMetadataReviewUseCase UseCase,
        IMetadataReviewDecisionStore Store,
        LibraryState State)
    {
        public static Harness Create()
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
                        [new(
                            "work", "edition", "Title", ["Author"],
                            [new EditionMetadataIdentifier("isbn", "9780306406157")]) ]));
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
                [new("isbn", "9780306406157")], [], "Author/Title (1)");
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
