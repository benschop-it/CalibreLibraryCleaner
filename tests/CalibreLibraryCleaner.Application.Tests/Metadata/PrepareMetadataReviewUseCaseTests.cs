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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedRepairScopeLimitsColdAndWarmQueriesAndPreservesUnrelatedDecisions(bool multiple)
    {
        CalibreBook[] books = Enumerable.Range(1, 1000).Select(id => new CalibreBook(
            new(id), $"Generated Journey {id}", "Generated Writer",
            [new(new(id), id == 467 ? "Generated Writer" : "Generated Writer [12]", "Writer, Generated")],
            [], [], $"Generated/Book ({id})")).ToArray();
        LibraryStateGenerationId generation = new(Guid.NewGuid());
        LibraryState state = new(generation, new(0), LibraryStateStatus.Authoritative,
            new(new("generated", 1, "C:\\generated"), DateTimeOffset.UnixEpoch, books, []),
            DateTimeOffset.UnixEpoch, workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed, generation, new(0), LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch, new(generation, new(0))));
        FinalAuthorReviewWorkspace identity = PrepareFinalAuthorReviewUseCase.Execute(state);
        CalibreBookId[] selected = multiple ? [new(467), new(901)] : [new(467)];
        EditionMetadataProviderIdentity providerIdentity = new("provider", "1.0");
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(providerIdentity);
        List<long> providerBooks = [];
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EditionMetadataLookupQuery query = call.GetArgument<EditionMetadataLookupQuery>(0)!;
                providerBooks.Add(query.BookId.Value);
                return Task.FromResult(new EditionMetadataSearchResult(providerIdentity, EditionMetadataSearchStatus.Success,
                    DateTimeOffset.UnixEpoch, [new("generated-work", $"edition-{query.BookId.Value}",
                        query.Title!, ["Generated Writer"], publisher: "Generated Publisher")]));
            });
        Dictionary<CalibreBookId, EditionMetadataProposal> cached = [];
        List<long> cacheReads = [];
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        A.CallTo(() => cache.TryReadAsync(A<EditionMetadataLookupQuery>._,
                A<DateTimeOffset>._, A<CancellationToken>._)).ReturnsLazily(call =>
            {
                CalibreBookId id = call.GetArgument<EditionMetadataLookupQuery>(0)!.BookId;
                cacheReads.Add(id.Value);
                return Task.FromResult(cached.GetValueOrDefault(id));
            });
        A.CallTo(() => cache.WriteAsync(A<EditionMetadataLookupQuery>._,
                A<EditionMetadataProposal>._, A<CancellationToken>._)).Invokes(call =>
            {
                EditionMetadataProposal proposal = call.GetArgument<EditionMetadataProposal>(1)!;
                cached[proposal.BookId] = proposal;
            }).Returns(Task.CompletedTask);
        MetadataReviewDecision other = new(new(new("metadata-review/book/2"),
            FusedEditionMetadataProposal.CurrentPolicyVersion, new(providerIdentity, "other-edition"),
            generation, state.Revision), true);
        List<MetadataReviewDecision> saved = [other];
        IMetadataReviewDecisionStore store = A.Fake<IMetadataReviewDecisionStore>();
        A.CallTo(() => store.ReadAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult<IReadOnlyList<MetadataReviewDecision>>(saved.ToArray()));
        A.CallTo(() => store.WriteAsync(A<string>._, A<IReadOnlyList<MetadataReviewDecision>>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                MetadataReviewDecision[] next = call.GetArgument<IReadOnlyList<MetadataReviewDecision>>(1)!.ToArray();
                saved.Clear();
                saved.AddRange(next);
            }).Returns(Task.CompletedTask);
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(DateTimeOffset.UnixEpoch);
        PrepareMetadataReviewUseCase useCase = new(new([provider], cache, clock, new()), store);

        MetadataReviewWorkspace first = await useCase.ExecuteAsync(state, [], null, CancellationToken.None, identity, selected);

        first.Subjects.Select(value => value.Subject.TargetBookId).Should().BeEquivalentTo(selected);
        first.Subjects.Should().OnlyContain(value => !value.Apply);
        first.ProviderResults!.InputIdentities!.Keys.Should().BeEquivalentTo(selected);
        first.ProviderResults.Providers.Sum(value => value.ProviderRequests).Should().Be(selected.Length);
        providerBooks.Should().BeEquivalentTo(selected.Select(value => value.Value));
        cacheReads.Should().OnlyContain(id => selected.Select(value => value.Value).Contains(id));
        saved.Should().Equal(other);
        MetadataReviewWorkspace applied = await useCase.SetApplyAsync(
            first, first.Subjects.First(value => value.Subject.Proposal.Candidate is not null).Subject.Id,
            true, CancellationToken.None);
        applied.ProviderResults.Should().BeSameAs(first.ProviderResults);
        saved.Should().Contain(other);
        MetadataReviewWorkspace cleared = await useCase.SetAllApplyAsync(applied, false, CancellationToken.None);
        cleared.ProviderResults.Should().BeSameAs(first.ProviderResults);
        saved.Should().Equal(other);
        int requestsBefore = providerBooks.Count;

        MetadataReviewWorkspace warm = await useCase.ExecuteAsync(state, [], null, CancellationToken.None, identity, selected);

        providerBooks.Should().HaveCount(requestsBefore);
        warm.ProviderResults!.Providers.Sum(value => value.ProviderRequests).Should().Be(0);
        warm.ProviderResults.Providers.Sum(value => value.CacheHits).Should().Be(selected.Length);
        warm.Subjects.Should().HaveCount(selected.Length).And.OnlyContain(value => !value.Apply);
        saved.Should().Equal(other);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedLocalRepairNeverPublishesUnselectedLocalCandidatesAndStaysUncheckedOnDecisionFailure(bool readFails)
    {
        FormatFileFingerprint fingerprint = new(100, new(new string('b', 64)));
        CalibreBook Book(int id) => new(new(id), "Broken Label", "Writer, Reliable",
            [new(new(id), "Reliable Writer", "Writer, Reliable")], [],
            [new("EPUB", "book", $"Writer/Book ({id})/book.epub", FormatFileStatus.Present, fingerprint,
                new(100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))], $"Writer/Book ({id})");
        CalibreBook[] books = [Book(467), Book(901)];
        LibraryStateGenerationId generation = new(Guid.NewGuid());
        LibraryState state = new(generation, new(0), LibraryStateStatus.Authoritative,
            new(new("generated", 1, "C:\\generated"), DateTimeOffset.UnixEpoch, books, []),
            DateTimeOffset.UnixEpoch, workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed, generation, new(0), LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch, new(generation, new(0))));
        FrontMatterAuthorObservation observation = new(
            "EPUB", fingerprint, FrontMatterAuthorVersions.EpubExtractor, FrontMatterAuthorVersions.Limits,
            FrontMatterAuthorStatus.Available,
            [new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 2, "By")], 1, 1, 100, false,
            identityCandidates: [new("Corrected Title", ["Reliable Writer"], 1, 1, 2, true)]);
        FinalAuthorReviewWorkspace identity = PrepareFinalAuthorReviewUseCase.Execute(
            state, frontMatterObservations: [observation]);
        IMetadataReviewDecisionStore store = A.Fake<IMetadataReviewDecisionStore>();
        if (readFails)
            A.CallTo(() => store.ReadAsync(A<string>._, A<CancellationToken>._)).Throws(new IOException("generated failure"));
        PrepareMetadataReviewUseCase useCase = new(
            new([], A.Fake<IEditionMetadataProposalCache>(), A.Fake<IClock>(), new()), store);

        MetadataReviewWorkspace result = await useCase.ExecuteAsync(state, [], null, CancellationToken.None, identity, [new(467)]);

        ReviewedMetadataSubject row = result.Subjects.Should().ContainSingle().Subject;
        row.Subject.TargetBookId.Should().Be(new CalibreBookId(467));
        row.Subject.Proposal.Candidate!.Title.Should().Be("Corrected Title");
        row.Apply.Should().BeFalse();
        result.ProviderResults!.Providers.Single().Proposals.Keys.Should().Equal(new CalibreBookId(467));
        result.PersistenceAvailable.Should().Be(!readFails);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("without-evidence")]
    public async Task InvalidRepairScopeNeverFallsBackToWholeLibraryOrStartsIo(string kind)
    {
        Harness harness = Harness.Create();
        LibraryState old = harness.State;
        LibraryState state = new(old.GenerationId, old.Revision, old.Status, old.Snapshot, old.ProjectedAtUtc,
            workflowCheckpoint: new(LibraryWorkflowPhase.Completed, old.GenerationId, old.Revision,
                LibraryWorkflowPolicyVersions.Current, old.ProjectedAtUtc, new(old.GenerationId, old.Revision)));
        FinalAuthorReviewWorkspace identity = PrepareFinalAuthorReviewUseCase.Execute(state);
        CalibreBookId[]? selected = kind switch
        {
            "absent" => null,
            "empty" => [],
            "duplicate" => [new(1), new(1)],
            "unknown" => [new(467)],
            _ => [new(1)],
        };
        Func<Task> act = () => harness.UseCase.ExecuteAsync(state, [], null, CancellationToken.None,
            kind == "without-evidence" ? null : identity, selected);

        await act.Should().ThrowAsync<ArgumentException>();

        A.CallTo(() => harness.Provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Cache.TryReadAsync(A<EditionMetadataLookupQuery>._,
            A<DateTimeOffset>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Store.ReadAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("A.C. Baantjer")]
    [InlineData("Baantjer, A.C.")]
    public async Task CachedCompactInitialsCannotReopenAlreadyCanonicalCurrentAuthorWork(string providerAuthor)
    {
        Harness harness = Harness.Create(alreadyApplied: true, currentAuthors: ["A. C. Baantjer"]);
        EditionMetadataCandidate candidate = new("work", "edition", "Title",
            [providerAuthor], [new("isbn", "9780306406157")], "Provider Publisher");
        UseCachedCandidate(harness, candidate);

        MetadataReviewWorkspace first = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        MetadataReviewWorkspace repeated = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);

        first.Subjects.Single().IsAlreadyApplied.Should().BeTrue();
        repeated.Subjects.Single().IsAlreadyApplied.Should().BeTrue();
        repeated.Subjects.Single().Apply.Should().BeFalse();
        repeated.Subjects.Single().Subject.Proposal.Candidate!.Authors.Should().Equal(providerAuthor);
        A.CallTo(() => harness.Provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task CachedSurnameFirstCoauthorsAreAlreadyAppliedWhenCurrentDisplayNamesAreNormalized()
    {
        Harness harness = Harness.Create(alreadyApplied: true,
            currentAuthors: ["Stacy Lannert", "Kristen Kemp"]);
        EditionMetadataCandidate candidate = new("work", "edition", "Title",
            ["Lannert, Stacy", "Kemp, Kristen"], [new("isbn", "9780306406157")], "Provider Publisher");
        UseCachedCandidate(harness, candidate);

        MetadataReviewWorkspace first = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        MetadataReviewWorkspace repeated = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);

        first.Subjects.Single().IsAlreadyApplied.Should().BeTrue();
        repeated.Subjects.Single().IsAlreadyApplied.Should().BeTrue();
        repeated.Subjects.Single().Apply.Should().BeFalse();
        repeated.Subjects.Single().Subject.Proposal.Candidate!.Authors.Should().Equal(candidate.Authors);
        A.CallTo(() => harness.Provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData("Author [pseud. ] [pseud.]")]
    [InlineData("Author psued. van Another Writer")]
    public async Task CachedAnnotatedAuthorsConvergeWithoutRequestsOrRestoringStaleApplyOverrides(string author)
    {
        Harness harness = Harness.Create(alreadyApplied: true);
        EditionMetadataCandidate candidate = new(
            "work", "edition", "Title", [author],
            [new("isbn", "9780306406157")], "Provider Publisher");
        UseCachedCandidate(harness, candidate);
        MetadataReviewWorkspace first = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        MetadataReviewSubject subject = first.Subjects.Single().Subject;
        MetadataReviewDecisionKey currentKey = MetadataReviewDecisionPolicy.CreateKey(
            subject, harness.State.GenerationId, harness.State.Revision);
        MetadataReviewDecision oldOverride = new(new(
            currentKey.SubjectId, "edition-metadata-fusion/1.5.0", currentKey.EditionIdentity,
            currentKey.GenerationId, currentKey.Revision), true);
        A.CallTo(() => harness.Store.ReadAsync(A<string>._, A<CancellationToken>._)).Returns([oldOverride]);

        MetadataReviewWorkspace second = await harness.UseCase.ExecuteAsync(
            harness.State, [], null, CancellationToken.None);
        MetadataReviewWorkspace checkedAll = await harness.UseCase.SetAllApplyAsync(
            second, true, CancellationToken.None);

        first.Subjects.Single().IsAlreadyApplied.Should().BeTrue();
        checkedAll.Subjects.Single().IsAlreadyApplied.Should().BeTrue();
        checkedAll.Subjects.Single().Apply.Should().BeFalse();
        checkedAll.Subjects.Single().IsOverride.Should().BeFalse();
        second.Subjects.Single().Subject.Proposal.Candidate!.Authors.Should().Equal(author);
        A.CallTo(() => harness.Provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Store.WriteAsync(
            A<string>._, A<IReadOnlyList<MetadataReviewDecision>>.That.Matches(value => value.Count == 0),
            A<CancellationToken>._)).MustHaveHappened();
    }

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
            state, [], null, CancellationToken.None, identity, [book.Id]);

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

    private static void UseCachedCandidate(Harness harness, EditionMetadataCandidate candidate) =>
        A.CallTo(() => harness.Cache.TryReadAsync(
                A<EditionMetadataLookupQuery>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EditionMetadataLookupQuery query = call.GetArgument<EditionMetadataLookupQuery>(0)!;
                return Task.FromResult<EditionMetadataProposal?>(new(
                    query.BookId, query.Provider, query.Fields, DateTimeOffset.UnixEpoch,
                    EditionMetadataProposalStatus.Proposed, candidate, 10_001, ["METADATA.EDITION.ISBN_EXACT"]));
            });

    private sealed record Harness(
        PrepareMetadataReviewUseCase UseCase,
        IMetadataReviewDecisionStore Store,
        LibraryState State,
        IEditionMetadataProvider Provider,
        IEditionMetadataProposalCache Cache)
    {
        public static Harness Create(
            bool alreadyApplied = false,
            bool riskyProposal = false,
            IReadOnlyList<string>? currentAuthors = null)
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
                new(1), "Title", "Author",
                (currentAuthors ?? ["Author"]).Select((name, index) => new BookAuthor(new(index + 1), name, name)),
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
            return new(useCase, store, state, provider, cache);
        }
    }
}
