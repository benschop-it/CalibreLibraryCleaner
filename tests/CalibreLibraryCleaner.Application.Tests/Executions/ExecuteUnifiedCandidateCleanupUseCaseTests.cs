using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Metadata;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

public sealed class ExecuteUnifiedCandidateCleanupUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecutesOneWorkerMarkerTypedDeltasCheckpointAndPostCleanupPhase()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "PDF", 'b')),
        ]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        result.TransferredFormatCount.Should().Be(1);
        result.RemovedFormatCount.Should().Be(1);
        result.RemovedRecordCount.Should().Be(1);
        harness.Logger.Messages.Should().Contain(value =>
            value.Contains("Outcome=Completed", StringComparison.Ordinal)
            && value.Contains("TransferredFormats=1", StringComparison.Ordinal)
            && value.Contains("RemovedFormats=1", StringComparison.Ordinal)
            && value.Contains("RemovedRecords=1", StringComparison.Ordinal)
            && value.Contains("TotalMilliseconds=", StringComparison.Ordinal));
        harness.Trace.Should().Equal(
            "TransferFormat:2:1:PDF",
            "RemoveFormat:2::PDF",
            "RemoveRecord:2::",
            "checkpoint",
            "candidate-cleanup-completed");
        LibraryState final = harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!;
        final.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidateCleanupCompleted);
        final.Snapshot.Books.Should().ContainSingle(value => value.Id == new CalibreBookId(1));
        A.CallTo(() => harness.Store.WriteMutationIntentAsync(
            harness.Snapshot.Identity.LibraryRoot,
            A<LibraryStateMutationIntent>.That.Matches(value => value.OperationCount == 3),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Store.AppendDeltaBatchAsync(
            harness.Snapshot.Identity.LibraryRoot,
            A<IReadOnlyList<LibraryStateDelta>>.That.Matches(value => value.Count == 3),
            A<LibraryState>._,
            false,
            A<string>._,
            true,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Store.CompactAsync(
            harness.Snapshot.Identity.LibraryRoot,
            A<LibraryState>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Store.WriteWorkflowCheckpointAsync(
            harness.Snapshot.Identity.LibraryRoot,
            A<LibraryState>.That.Matches(value =>
                value.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.CandidateCleanupCompleted),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CheckedMetadataRunsAfterCleanupAndProjectsVerifiedReadBack()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(Candidate());

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        CalibreMutationOperation[] operations = harness.Chunks.SelectMany(value => value.Operations).ToArray();
        operations.Should().HaveCount(8);
        operations.Should().OnlyContain(value => value.Kind == CalibreMutationOperationKind.SetMetadata);
        operations.Single(value => value.Metadata?.Field == LibraryMetadataField.Identifiers)
            .Metadata!.Values.Should().Equal("isbn:9780140328721");
        CalibreBook keeper = harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!
            .Snapshot.Books.Single();
        keeper.Title.Should().Be("Qualified Title");
        keeper.Authors.Select(value => value.Name).Should().Equal("Alpha Author", "Beta Author");
        keeper.AuthorSort.Should().Be("Verified Calibre Author Sort");
        keeper.RelativeDirectory.Should().Be("Verified/Managed/Path");
        keeper.Identifiers.Should().Contain(value => value.Type == "local" && value.Value == "preserved");
        keeper.PublicationMetadata.Should().BeEquivalentTo(new BookPublicationMetadata(
            "Qualified Publisher",
            new DateTimeOffset(2004, 5, 6, 0, 0, 0, TimeSpan.Zero),
            "Qualified Series",
            3.5m,
            ["eng"],
            false));
        A.CallTo(() => harness.Store.WriteMutationIntentAsync(
            harness.Snapshot.Identity.LibraryRoot,
            A<LibraryStateMutationIntent>.That.Matches(value => value.OperationCount == 8),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CompletedWorkflowNormalizesConservativeAuthorNamesAndSorts()
    {
        CalibreBook book = new(
            new(1),
            "Book",
            "Pohl, Frederik & Williamson, Jack",
            [
                new(new(1), "Pohl| Frederik", "Pohl, Frederik"),
                new(new(2), "Williamson| Jack", "Williamson, Jack"),
            ],
            [],
            [Format(1, "EPUB", 'a')],
            "Pohl, Frederik/Book");
        Harness harness = await Harness.CreateAsync([book]);
        await harness.AdvanceToCompletedAsync();

        UnifiedCandidateCleanupResult result = await harness.ExecuteAuthorNormalizationAsync();

        result.State.Should().Be(
            UnifiedCandidateCleanupState.Completed,
            string.Join(" | ", result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        CalibreMutationOperation operation = harness.Chunks.SelectMany(value => value.Operations)
            .Should().ContainSingle().Subject;
        operation.Metadata!.Values.Should().Equal("Frederik Pohl", "Jack Williamson");
        operation.Metadata.AuthorSortValues.Should().Equal("Pohl, Frederik", "Williamson, Jack");
        CalibreBook projected = harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!
            .Snapshot.Books.Single();
        projected.Authors.Select(value => value.Name).Should().Equal("Frederik Pohl", "Jack Williamson");
        projected.Authors.Select(value => value.SortName).Should().Equal("Pohl, Frederik", "Williamson, Jack");
        projected.AuthorSort.Should().Be("Pohl, Frederik & Williamson, Jack");
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.WorkflowCheckpoint.Phase
            .Should().Be(LibraryWorkflowPhase.Completed);
    }

    [Fact]
    public async Task StrictCombinedCommaAuthorsBecomeSeparateDisplayAuthors()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(
            Candidate(authors: ["Pohl, Frederik & Williamson, Jack"]));

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        CalibreMutationOperation authors = harness.Chunks.SelectMany(value => value.Operations)
            .Single(value => value.Metadata?.Field == LibraryMetadataField.Authors);
        authors.Metadata!.Values.Should().Equal("Frederik Pohl", "Jack Williamson");
    }

    [Fact]
    public async Task AmbiguousCombinedAuthorsAreOmittedWhileOtherMetadataCompletes()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(
            Candidate(authors: ["One & Two"]));

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        result.Issues.Should().ContainSingle(value =>
            value.Code == "CANDIDATE.METADATA_AUTHORS_OMITTED"
            && value.Severity == ExecutionIssueSeverity.Warning);
        result.OmittedMetadataAuthorFieldCount.Should().Be(1);
        harness.Chunks.SelectMany(value => value.Operations).Should().NotContain(value =>
            value.Metadata != null && value.Metadata.Field == LibraryMetadataField.Authors);
        result.UpdatedMetadataFieldCount.Should().BePositive();
    }

    [Fact]
    public async Task CheckedSingletonMetadataRunsForRetainedMemberOfSkippedCandidateGroup()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "EPUB", 'b')),
        ]);
        await harness.AdvanceToMetadataAsync();

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataWorkspaceAsync(
            harness.MetadataWorkspace(Candidate(), new(1)));

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        harness.Chunks.SelectMany(value => value.Operations).Should().OnlyContain(value =>
            value.Kind == CalibreMutationOperationKind.SetMetadata
            && value.RecordId == new CalibreBookId(1));
    }

    [Fact]
    public async Task StaleMetadataWorkspaceFailsBeforeToolDiscovery()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
        ]);
        await harness.AdvanceToMetadataAsync();
        MetadataReviewWorkspace current = harness.MetadataWorkspace(Candidate());
        MetadataReviewWorkspace stale = new(
            current.LibraryRoot,
            current.GenerationId,
            current.Revision.Next(),
            current.Subjects,
            true);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataWorkspaceAsync(stale);

        result.State.Should().Be(UnifiedCandidateCleanupState.PreflightFailed);
        result.Issues.Should().Contain(value => value.Code == "CANDIDATE.PLAN_INVALID");
        A.CallTo(() => harness.Tools.DiscoverAndProbeAsync(
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MetadataFailureMarksUncertainAndCommitsNoCleanupDelta()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);
        await harness.AdvanceToMetadataAsync();
        CalibreMutationChunkRequest? attempted = null;
        A.CallTo(() => harness.Worker.ExecuteChunkAsync(
                A<CalibreMutationChunkRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                attempted = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                return Task.FromResult(new CalibreMutationChunkResult(
                    attempted.ChunkId,
                    true,
                    attempted.Operations.Select((value, index) => new CalibreMutationOperationResult(
                        value.OperationId,
                        value.Kind,
                        false,
                        index == 0 ? "metadata_readback_failed" : "not_executed")).ToArray(),
                    "metadata_readback_failed"));
            });

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataWorkspaceAsync(
            harness.MetadataWorkspace(Candidate()));

        result.State.Should().Be(UnifiedCandidateCleanupState.PartiallyCompleted);
        result.Issues.Should().ContainSingle(value =>
            value.Code == "CANDIDATE.WORKER_CHUNK_FAILED"
            && value.Explanation.Contains("metadata_readback_failed", StringComparison.Ordinal)
            && value.Explanation.Contains("completed 0 of", StringComparison.Ordinal));
        harness.Logger.Messages.Should().ContainSingle(value =>
            value.Contains("FailureCode=metadata_readback_failed", StringComparison.Ordinal)
            && value.Contains("ChunkOperation=1", StringComparison.Ordinal)
            && value.Contains("OperationKind=Title", StringComparison.Ordinal)
            && value.Contains("UncommittedSuccessfulPrefix=0", StringComparison.Ordinal));
        attempted.Should().NotBeNull();
        attempted!.Operations[0].Kind.Should().Be(CalibreMutationOperationKind.SetMetadata);
        attempted.Operations.Should().OnlyContain(value => value.Kind == CalibreMutationOperationKind.SetMetadata);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        A.CallTo(() => harness.Store.AppendDeltaBatchAsync(
            A<string>._,
            A<IReadOnlyList<LibraryStateDelta>>._,
            A<LibraryState>._,
            A<bool>._,
            A<string?>._,
            A<bool>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CheckedSingletonExecutesMetadataWithoutCleanupOperations()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(Candidate());

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        harness.Chunks.SelectMany(value => value.Operations)
            .Should().OnlyContain(value => value.Kind == CalibreMutationOperationKind.SetMetadata);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot.Books
            .Should().ContainSingle(value => value.Title == "Qualified Title");
    }

    [Fact]
    public async Task UnsupportedProviderLanguageIsOmittedAndLocalLanguageIsPreserved()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(
            Candidate(languages: ["zzz"]));

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        harness.Chunks.SelectMany(value => value.Operations)
            .Where(value => value.Metadata is not null)
            .Select(value => value.Metadata!.Field)
            .Should().NotContain(LibraryMetadataField.Languages);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot.Books.Single()
            .PublicationMetadata.Languages.Should().Equal("eng");
    }

    [Fact]
    public async Task VerifiedUnchangedMetadataSkipsCompleteWithoutFalseUpdates()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);
        A.CallTo(() => harness.Worker.ExecuteChunkAsync(
                A<CalibreMutationChunkRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                return Task.FromResult(new CalibreMutationChunkResult(
                    chunk.ChunkId,
                    true,
                    chunk.Operations.Select(value => new CalibreMutationOperationResult(
                        value.OperationId,
                        value.Kind,
                        true,
                        IsSkipped: true,
                        SkipCode: "metadata_values_invalid")).ToArray()));
            });

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(Candidate());

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        result.UpdatedMetadataFieldCount.Should().Be(0);
        result.SkippedMetadataFieldCount.Should().BePositive();
        result.Issues.Should().ContainSingle(value =>
            value.Code == "CANDIDATE.METADATA_SKIPPED_UNCHANGED");
        harness.Logger.Messages.Should().ContainSingle(value =>
            value.Contains("SkipCode=metadata_values_invalid", StringComparison.Ordinal)
            && value.Contains($"skipped {result.SkippedMetadataFieldCount}", StringComparison.Ordinal));
        A.CallTo(() => harness.Store.AppendDeltaBatchAsync(
                A<string>._,
                A<IReadOnlyList<LibraryStateDelta>>.That.Matches(values =>
                    values.Count == result.SkippedMetadataFieldCount
                    && values.All(value => value is SkipMetadataLibraryStateDelta)),
                A<LibraryState>._,
                A<bool>._,
                A<string>._,
                true,
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CheckedCoverUsesStagedWorkerPayloadAndProjectsVerifiedCover()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(Candidate(withCover: true));

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        CalibreMutationOperation cover = harness.Chunks.SelectMany(value => value.Operations)
            .Single(value => value.Metadata?.Field == LibraryMetadataField.Cover);
        cover.Metadata!.StagedCoverFileName.Should().Be("cover-00000.jpg");
        cover.Metadata.StagedCoverFingerprint.Should().NotBeNull();
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Snapshot.Books.Single()
            .PublicationMetadata.HasCover.Should().BeTrue();
        A.CallTo(() => harness.Workers.TryOpenAsync(
            A<OpenCalibreMutationWorkerRequest>.That.Matches(value =>
                value.MetadataCoverStagingRoot == "C:\\staged-covers"),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CoverStagingFailureReportsProgressBeforeToolDiscovery()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);
        A.CallTo(() => harness.CoverStager.StageAsync(
                A<IReadOnlyList<EditionCoverStagingRequest>>._,
                A<IProgress<EditionCoverStagingProgress>?>._,
                A<CancellationToken>._))
            .Returns(new EditionCoverStagingResult(null, "METADATA_COVER.RESPONSE_INVALID", 7, 11));

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(Candidate(withCover: true));

        result.State.Should().Be(UnifiedCandidateCleanupState.PreflightFailed);
        result.Issues.Should().ContainSingle(value =>
            value.Code == "CANDIDATE.METADATA_COVER_STAGING_FAILED"
            && value.Explanation.Contains("completed 7 of 11", StringComparison.Ordinal));
        A.CallTo(() => harness.Tools.DiscoverAndProbeAsync(
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MoreThanOneHundredOperationsUseBoundedChunksOnOneWorker()
    {
        CalibreBook[] books = Enumerable.Range(1, 52)
            .Select(id => Book(id, Format(id, "EPUB", (char)('a' + id % 6))))
            .ToArray();
        Harness harness = await Harness.CreateAsync(books);

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        harness.Chunks.Select(value => value.Operations.Count).Should().Equal(100, 2);
        A.CallTo(() => harness.Workers.TryOpenAsync(
            A<OpenCalibreMutationWorkerRequest>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AllSkippedAdvancesCompletedWithoutToolWorkerOrMarker()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "EPUB", 'b')),
        ]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync(skip: true);

        result.State.Should().Be(UnifiedCandidateCleanupState.NothingToDo);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.WorkflowCheckpoint.Phase
            .Should().Be(LibraryWorkflowPhase.CandidateCleanupCompleted);
        A.CallTo(() => harness.Tools.DiscoverAndProbeAsync(
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Store.WriteMutationIntentAsync(
            A<string>._, A<LibraryStateMutationIntent>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AllMetadataUncheckedAdvancesCompletedWithoutToolWorkerOrMarker()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);
        await harness.AdvanceToMetadataAsync();
        MetadataReviewWorkspace current = harness.MetadataWorkspace(Candidate());
        ReviewedMetadataSubject selected = current.Subjects.Single();
        MetadataReviewDecision decision = MetadataReviewDecisionPolicy.CreateOverride(
            selected.Subject, current.GenerationId, current.Revision, apply: false)!;
        MetadataReviewWorkspace uncheckedWorkspace = new(
            current.LibraryRoot,
            current.GenerationId,
            current.Revision,
            [new(selected.Subject, false, true, decision.Key)],
            true);

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataWorkspaceAsync(uncheckedWorkspace);

        result.State.Should().Be(UnifiedCandidateCleanupState.NothingToDo);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.WorkflowCheckpoint.Phase
            .Should().Be(LibraryWorkflowPhase.Completed);
        A.CallTo(() => harness.Tools.DiscoverAndProbeAsync(
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => harness.Store.WriteMutationIntentAsync(
            A<string>._, A<LibraryStateMutationIntent>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MissingBackupBlocksBeforeToolDiscovery()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "EPUB", 'b')),
        ]);

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync(backup: false);

        result.State.Should().Be(UnifiedCandidateCleanupState.PreflightFailed);
        result.Issues.Should().Contain(value => value.Code == "CANDIDATE.BACKUP_NOT_CONFIRMED");
        A.CallTo(() => harness.Tools.DiscoverAndProbeAsync(
            A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AmbiguousWorkerChunkMarksStateUncertainAndStops()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "PDF", 'b')),
        ]);
        A.CallTo(() => harness.Worker.ExecuteChunkAsync(
                A<CalibreMutationChunkRequest>._, A<CancellationToken>._))
            .Returns(new CalibreMutationChunkResult(
                "failed", true, [], "AMBIGUOUS"));

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(UnifiedCandidateCleanupState.PartiallyCompleted);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        A.CallTo(() => harness.Worker.ExecuteChunkAsync(
            A<CalibreMutationChunkRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CheckpointFailureAfterMutationMarksStateUncertainAndDoesNotComplete()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "PDF", 'b')),
        ]);
        A.CallTo(() => harness.Store.CompactAsync(
                A<string>._, A<LibraryState>._, A<CancellationToken>._))
            .ThrowsAsync(new IOException("controlled checkpoint failure"));

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(UnifiedCandidateCleanupState.PartiallyCompleted);
        result.Issues.Should().Contain(value => value.Code == "CANDIDATE.CHECKPOINT_FAILED");
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        A.CallTo(() => harness.Store.WriteWorkflowCheckpointAsync(
            A<string>._,
            A<LibraryState>.That.Matches(value =>
                value.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.Completed),
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TransientlyUnavailableCoverIsOmittedWhileOtherMetadataCompletes()
    {
        Harness harness = await Harness.CreateAsync([Book(1, Format(1, "EPUB", 'a'))]);
        IEditionCoverStagingSession session = A.Fake<IEditionCoverStagingSession>();
        A.CallTo(() => session.Covers).Returns(new Dictionary<string, StagedEditionCover>());
        A.CallTo(() => harness.CoverStager.StageAsync(
                A<IReadOnlyList<EditionCoverStagingRequest>>._,
                A<IProgress<EditionCoverStagingProgress>?>._,
                A<CancellationToken>._))
            .Returns(new EditionCoverStagingResult(
                session,
                null,
                1,
                1,
                new Dictionary<string, string> { ["metadata-subject"] = "METADATA_COVER.HTTP_FAILED" }));

        UnifiedCandidateCleanupResult result = await harness.ExecuteMetadataAsync(Candidate(withCover: true));

        result.State.Should().Be(UnifiedCandidateCleanupState.Completed);
        result.OmittedMetadataCoverCount.Should().Be(1);
        result.Issues.Should().ContainSingle(value =>
            value.Code == "CANDIDATE.METADATA_COVERS_OMITTED"
            && value.Severity == ExecutionIssueSeverity.Warning);
        harness.Chunks.SelectMany(value => value.Operations)
            .Should().NotContain(value =>
                value.Metadata != null && value.Metadata.Field == LibraryMetadataField.Cover);
        result.UpdatedMetadataFieldCount.Should().BePositive();
        harness.Logger.Messages.Should().ContainSingle(value =>
            value.Contains("omitted 1", StringComparison.Ordinal)
            && value.Contains("FailureCode=METADATA_COVER.HTTP_FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeltaPersistenceFailureMarksStateUncertainAndStops()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "PDF", 'b')),
        ]);
        A.CallTo(() => harness.Store.AppendDeltaBatchAsync(
                A<string>._,
                A<IReadOnlyList<LibraryStateDelta>>._,
                A<LibraryState>._,
                A<bool>._,
                A<string?>._,
                A<bool>._,
                A<CancellationToken>._))
            .ThrowsAsync(new IOException("controlled delta failure"));

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(UnifiedCandidateCleanupState.PartiallyCompleted);
        result.Issues.Should().Contain(value => value.Code == "CANDIDATE.DELTA_COMMIT_FAILED");
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        A.CallTo(() => harness.Worker.ExecuteChunkAsync(
            A<CalibreMutationChunkRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PostCleanupPhasePersistenceFailureMarksStateUncertain()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "PDF", 'b')),
        ]);
        A.CallTo(() => harness.Store.WriteWorkflowCheckpointAsync(
                A<string>._,
                A<LibraryState>.That.Matches(value =>
                    value.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.CandidateCleanupCompleted),
                A<CancellationToken>._))
            .ThrowsAsync(new IOException("controlled completion failure"));

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync();

        result.State.Should().Be(UnifiedCandidateCleanupState.PartiallyCompleted);
        result.Issues.Should().Contain(value => value.Code == "CANDIDATE.COMPLETION_PERSIST_FAILED");
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        A.CallTo(() => harness.Store.CompactAsync(
            A<string>._, A<LibraryState>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CancellationAfterMarkerMarksStateUncertainBeforeFirstChunk()
    {
        Harness harness = await Harness.CreateAsync([
            Book(1, Format(1, "EPUB", 'a')),
            Book(2, Format(2, "PDF", 'b')),
        ]);
        using CancellationTokenSource cancellation = new();
        A.CallTo(() => harness.Store.WriteMutationIntentAsync(
                A<string>._, A<LibraryStateMutationIntent>._, A<CancellationToken>._))
            .Invokes(cancellation.Cancel)
            .Returns(Task.CompletedTask);

        UnifiedCandidateCleanupResult result = await harness.ExecuteAsync(cancellationToken: cancellation.Token);

        result.State.Should().Be(UnifiedCandidateCleanupState.PartiallyCompleted);
        harness.State.GetCurrent(harness.Snapshot.Identity.LibraryRoot)!.Status
            .Should().Be(LibraryStateStatus.Uncertain);
        A.CallTo(() => harness.Worker.ExecuteChunkAsync(
            A<CalibreMutationChunkRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    private sealed class Harness
    {
        private readonly ExecuteUnifiedCandidateCleanupUseCase _useCase;
        private readonly LibraryState _initial;

        private Harness(
            LibrarySnapshot snapshot,
            LibraryState initial,
            LibraryStateSession state,
            ILibraryStateStore store,
            ICalibreToolDiscovery tools,
            ICalibreMutationWorkerFactory workers,
            ICalibreMutationWorkerSession worker,
            IEditionCoverStager coverStager,
            ExecuteUnifiedCandidateCleanupUseCase useCase,
            CapturingLogger<ExecuteUnifiedCandidateCleanupUseCase> logger,
            List<string> trace,
            List<CalibreMutationChunkRequest> chunks)
        {
            Snapshot = snapshot;
            _initial = initial;
            State = state;
            Store = store;
            Tools = tools;
            Workers = workers;
            Worker = worker;
            CoverStager = coverStager;
            _useCase = useCase;
            Logger = logger;
            Trace = trace;
            Chunks = chunks;
        }

        public LibrarySnapshot Snapshot { get; }
        public LibraryStateSession State { get; }
        public ILibraryStateStore Store { get; }
        public ICalibreToolDiscovery Tools { get; }
        public ICalibreMutationWorkerFactory Workers { get; }
        public ICalibreMutationWorkerSession Worker { get; }
        public IEditionCoverStager CoverStager { get; }
        public CapturingLogger<ExecuteUnifiedCandidateCleanupUseCase> Logger { get; }
        public List<string> Trace { get; }
        public List<CalibreMutationChunkRequest> Chunks { get; }

        public static async Task<Harness> CreateAsync(CalibreBook[] books)
        {
            UnifiedCandidateGroup[] unified = books.Length == 1
                ? []
                : UnifiedCandidateMergePolicy.Merge([], [WorkLanguageCandidateGroup.Create(
                    "en",
                    books.Select(value => value.Id),
                    [books[0].Id],
                    WorkLanguageCandidateConfidence.Strong,
                    [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
                    contentComparison: new(books.Length - 1, books.Length - 1, 0, 0, 0, 0))], books).ToArray();
            LibrarySnapshot snapshot = new(
                new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
                Now,
                books,
                [],
                unifiedCandidateGroups: unified);
            ILibraryStateStore store = A.Fake<ILibraryStateStore>();
            LibraryStateSession state = new(store);
            LibraryState exact = (await state.StartFromExactAnalysisAsync(
                snapshot, CancellationToken.None)).State!;
            LibraryState completed = (await state.AdvanceWorkflowAsync(
                snapshot.Identity.LibraryRoot,
                LibraryWorkflowPhase.CandidatePreparationReady,
                Now,
                CancellationToken.None)).State!;
            LibraryState refreshed = (await state.StartFromPostExactRefreshAsync(
                snapshot,
                new(completed.GenerationId, completed.Revision),
                CancellationToken.None)).State!;
            LibraryState analyzed = (await state.CompleteCandidateAnalysisAsync(
                snapshot, refreshed.GenerationId, refreshed.Revision, CancellationToken.None)).State!;
            exact.GenerationId.Should().NotBe(analyzed.GenerationId);
            Fake.ClearRecordedCalls(store);

            List<string> trace = [];
            List<CalibreMutationChunkRequest> chunks = [];
            A.CallTo(() => store.CompactAsync(
                    snapshot.Identity.LibraryRoot, A<LibraryState>._, A<CancellationToken>._))
                .Invokes(() => trace.Add("checkpoint"));
            A.CallTo(() => store.WriteWorkflowCheckpointAsync(
                    snapshot.Identity.LibraryRoot,
                    A<LibraryState>.That.Matches(value =>
                        value.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.CandidateCleanupCompleted),
                    A<CancellationToken>._))
                .Invokes(() => trace.Add("candidate-cleanup-completed"));
            ICalibreMutationWorkerSession worker = A.Fake<ICalibreMutationWorkerSession>();
            A.CallTo(() => worker.ExecuteChunkAsync(
                    A<CalibreMutationChunkRequest>._, A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                    chunks.Add(chunk);
                    trace.AddRange(chunk.Operations.Select(value =>
                        $"{value.Kind}:{value.RecordId.Value}:{value.TargetRecordId?.Value}:{value.CanonicalFormat}"));
                    return Task.FromResult(new CalibreMutationChunkResult(
                        chunk.ChunkId,
                        true,
                        chunk.Operations.Select(value => new CalibreMutationOperationResult(
                            value.OperationId,
                            value.Kind,
                            true,
                            VerifiedMetadataValues: value.Metadata is null
                                ? null
                                : value.Metadata.Field == LibraryMetadataField.Identifiers
                                    ? [.. value.Metadata.Values, "local:preserved"]
                                    : value.Metadata.Field == LibraryMetadataField.Cover
                                        ? ["true"]
                                        : value.Metadata.Field == LibraryMetadataField.Languages
                                            ? ["eng"]
                                        : value.Metadata.Values,
                            VerifiedManagedPath: value.Metadata is null ? null : "Verified/Managed/Path",
                            VerifiedAuthorSort: value.Metadata is null
                                ? null
                                : value.Metadata.AuthorSortValues is null
                                    ? "Verified Calibre Author Sort"
                                    : string.Join(" & ", value.Metadata.AuthorSortValues),
                            VerifiedAuthorSortValues: value.Metadata?.AuthorSortValues)).ToArray()));
                });
            ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
            A.CallTo(() => workers.TryOpenAsync(
                    A<OpenCalibreMutationWorkerRequest>._, A<CancellationToken>._))
                .Returns(new CalibreMutationWorkerOpenResult(worker, null));
            ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
            CalibreToolDescriptor tool = new(
                "C:\\Calibre2\\calibredb.exe",
                new("C:\\Calibre2\\calibredb.exe", "9.11.0", new(new string('f', 64)),
                    "calibredb/windows/9.11.0"));
            A.CallTo(() => tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(tool, []));
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            ILibraryMutationLeaseHandle handle = A.Fake<ILibraryMutationLeaseHandle>();
            A.CallTo(() => handle.IsHeld).Returns(true);
            A.CallTo(() => lease.TryAcquireAsync(
                    A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(handle, []));
            ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
            A.CallTo(() => ids.Create()).Returns(new(
                Guid.Parse("99999999-8888-7777-6666-555555555555")));
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddSeconds(1));
            IEditionCoverStager coverStager = A.Fake<IEditionCoverStager>();
            A.CallTo(() => coverStager.StageAsync(
                    A<IReadOnlyList<EditionCoverStagingRequest>>._,
                    A<IProgress<EditionCoverStagingProgress>?>._,
                    A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    IReadOnlyList<EditionCoverStagingRequest> requests =
                        call.GetArgument<IReadOnlyList<EditionCoverStagingRequest>>(0)!;
                    Dictionary<string, StagedEditionCover> covers = requests.ToDictionary(
                        value => value.Key,
                        _ => new StagedEditionCover(
                            "cover-00000.jpg",
                            new(100, new(new string('c', 64)))),
                        StringComparer.Ordinal);
                    return Task.FromResult(new EditionCoverStagingResult(
                        new FakeCoverSession("C:\\staged-covers", covers), null));
                });
            CapturingLogger<ExecuteUnifiedCandidateCleanupUseCase> logger = new();
            ExecuteUnifiedCandidateCleanupUseCase useCase = new(
                state, tools, workers, lease, ids, coverStager, clock, logger);
            return new(snapshot, analyzed, state, store, tools, workers, worker, coverStager,
                useCase, logger, trace, chunks);
        }

        public Task<UnifiedCandidateCleanupResult> ExecuteAsync(
            bool skip = false,
            bool backup = true,
            MetadataReviewWorkspace? metadataReview = null,
            CancellationToken cancellationToken = default)
        {
            UnifiedCandidateCleanupSelection[] selections = Snapshot.UnifiedCandidateGroups.Select(group => new UnifiedCandidateCleanupSelection(
                group.Id,
                group.Members,
                group.GeneratedKeeperBookId,
                skip)).ToArray();
            return _useCase.ExecuteAsync(new(
                Snapshot.Identity.LibraryRoot,
                _initial.GenerationId,
                _initial.Revision,
                selections,
                backup,
                metadataReview), null, cancellationToken);
        }

        public async Task AdvanceToMetadataAsync()
        {
            if (State.GetCurrent(Snapshot.Identity.LibraryRoot)!.WorkflowCheckpoint.Phase
                == LibraryWorkflowPhase.CandidateAnalysisReady)
                await ExecuteAsync(skip: true);
            Trace.Clear();
            Chunks.Clear();
            Fake.ClearRecordedCalls(Store);
        }

        public async Task<UnifiedCandidateCleanupResult> ExecuteMetadataAsync(
            EditionMetadataCandidate candidate)
        {
            await AdvanceToMetadataAsync();
            return await ExecuteMetadataWorkspaceAsync(MetadataWorkspace(candidate));
        }

        public async Task AdvanceToCompletedAsync()
        {
            await AdvanceToMetadataAsync();
            LibraryState current = State.GetCurrent(Snapshot.Identity.LibraryRoot)!;
            LibraryStateSessionOutcome completed = await State.AdvanceWorkflowAsync(
                Snapshot.Identity.LibraryRoot,
                LibraryWorkflowPhase.Completed,
                current.WorkflowCheckpoint.PublishedAtUtc,
                CancellationToken.None);
            completed.IsSuccess.Should().BeTrue(completed.Explanation);
            Trace.Clear();
            Chunks.Clear();
            Fake.ClearRecordedCalls(Store);
        }

        public Task<UnifiedCandidateCleanupResult> ExecuteAuthorNormalizationAsync()
        {
            LibraryState current = State.GetCurrent(Snapshot.Identity.LibraryRoot)!;
            return _useCase.ExecuteAsync(new(
                Snapshot.Identity.LibraryRoot,
                current.GenerationId,
                current.Revision,
                [],
                true,
                NormalizeAuthors: true), null, CancellationToken.None);
        }

        public Task<UnifiedCandidateCleanupResult> ExecuteMetadataWorkspaceAsync(
            MetadataReviewWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            LibraryState current = State.GetCurrent(Snapshot.Identity.LibraryRoot)!;
            return _useCase.ExecuteAsync(new(
                Snapshot.Identity.LibraryRoot,
                current.GenerationId,
                current.Revision,
                [],
                true,
                workspace), null, cancellationToken);
        }

        public MetadataReviewWorkspace MetadataWorkspace(
            EditionMetadataCandidate candidate,
            CalibreBookId? targetBookId = null)
        {
            LibraryState current = State.GetCurrent(Snapshot.Identity.LibraryRoot)!;
            CalibreBookId target = targetBookId ?? current.Snapshot.Books.Single().Id;
            EditionMetadataProviderIdentity provider = new("qualified-provider", "1.0");
            EditionMetadataProposal providerProposal = new(
                target,
                provider,
                EditionMetadataQueryFields.Identifier,
                Now,
                EditionMetadataProposalStatus.Proposed,
                candidate,
                1_000,
                ["METADATA.QUALIFIED"]);
            FusedEditionMetadataProposal proposal = new(
                FusedEditionMetadataConfidence.High,
                provider,
                candidate,
                [providerProposal],
                [],
                ["METADATA.QUALIFIED"]);
            MetadataReviewSubject subject = new(
                new("metadata-subject"),
                null,
                [target],
                target,
                proposal);
            return new(
                Snapshot.Identity.LibraryRoot,
                current.GenerationId,
                current.Revision,
                [new ReviewedMetadataSubject(subject, true, false, null)],
                true);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static CalibreBook Book(long id, params BookFormat[] formats) => new(
        new(id),
        $"Book {id}",
        "Author",
        [new(new(id), "Author", "Author")],
        [],
        formats,
        $"Author/Book{id}",
        new(languages: ["eng"]));

    private static BookFormat Format(long id, string format, char digest) => new(
        format,
        "book",
        $"Author/Book{id}/book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        new(1_024, new(new string(digest, 64))),
        new(1_024, Now, Now, 0));

    private static EditionMetadataCandidate Candidate(
        bool withCover = false,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? authors = null) => new(
        "qualified-work",
        "qualified-edition",
        "Qualified Title",
        authors ?? ["Alpha Author", "Beta Author"],
        [new("isbn", "0140328726"), new("isbn", "9780140328721")],
        "Qualified Publisher",
        new(2004, 5, 6),
        languages ?? ["eng"],
        "Qualified Series",
        3.5m,
        withCover ? new("qualified-cover", "https://covers.openlibrary.org/b/id/123-L.jpg") : null);

    private sealed class FakeCoverSession(
        string stagingRoot,
        IReadOnlyDictionary<string, StagedEditionCover> covers) : IEditionCoverStagingSession
    {
        public string StagingRoot { get; } = stagingRoot;
        public IReadOnlyDictionary<string, StagedEditionCover> Covers { get; } = covers;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
