using System.Text.Json.Nodes;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LibrarySnapshots;

public sealed class VersionedJsonLibraryStateStoreTests
{
    [Fact]
    public async Task ListReadsManifestWithoutOpeningTheLargeBaseline()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState state = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        await store.WriteBaselineAsync(state, CancellationToken.None);
        File.Delete(Directory.GetFiles(cache, "*.baseline.json").Single());

        IReadOnlyList<CalibreLibraryCleaner.Application.Abstractions.PersistedLibraryStateInfo> listed =
            await store.ListAsync(CancellationToken.None);

        listed.Should().ContainSingle().Which.Should().Be(
            new CalibreLibraryCleaner.Application.Abstractions.PersistedLibraryStateInfo(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.Snapshot.Identity.LibraryRoot)),
            state.Snapshot.ScannedAt,
            state.ProjectedAtUtc,
            state.GenerationId,
            state.Revision,
            state.Status));
    }

    [Fact]
    public async Task PublishingNewGenerationPrunesPreviousGenerationFiles()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState first = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        LibraryState second = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        await store.WriteBaselineAsync(first, CancellationToken.None);

        await store.WriteBaselineAsync(second, CancellationToken.None);

        Directory.GetFiles(cache, "*.baseline.json").Should().ContainSingle()
            .Which.Should().Contain("11111111222233334444555555555555");
        Directory.GetFiles(cache, "*.deltas.jsonl").Should().ContainSingle()
            .Which.Should().Contain("11111111222233334444555555555555");
        (await store.ReadAsync(second.Snapshot.Identity.LibraryRoot, CancellationToken.None))
            .Should().BeEquivalentTo(second);
    }

    [Fact]
    public async Task WorkflowCheckpointReplaysAcrossStoreInstances()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore writer = new(new() { StorageRoot = cache });
        LibraryState state = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt);
        await writer.WriteBaselineAsync(state, CancellationToken.None);

        LibraryState? loaded = await new VersionedJsonLibraryStateStore(new() { StorageRoot = cache })
            .ReadAsync(fixture.Snapshot.Identity.LibraryRoot, CancellationToken.None);

        loaded!.WorkflowCheckpoint.Should().Be(state.WorkflowCheckpoint);
        loaded.IsWorkflowCheckpointCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task CompletedExactDeltasAndBaselineSurviveCheckpointCompactionForRefresh()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState preExact = LibraryState.FromScan(fixture.Snapshot,
                new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt);
        CalibreBook target = preExact.Snapshot.Books[0];
        FormatFileFingerprint fingerprint = new(42, new(new string('c', 64)));
        AddOrReplaceFormatLibraryStateDelta delta = new(
            preExact.GenerationId,
            preExact.Revision,
            "bulk-add:target:MOBI",
            preExact.ProjectedAtUtc.AddSeconds(1),
            target.Id,
            "MOBI",
            fingerprint,
            null);
        LibraryStateMutationIntent intent = new(
            "exact-run",
            preExact.GenerationId,
            preExact.Revision,
            1,
            preExact.ProjectedAtUtc.AddSeconds(1));
        await store.WriteBaselineAsync(preExact, CancellationToken.None);
        await store.WriteMutationIntentAsync(preExact.Snapshot.Identity.LibraryRoot, intent, CancellationToken.None);
        LibraryState projected = LibraryStateDeltaPolicy.Apply(preExact, delta);
        await store.AppendDeltaBatchAsync(
            preExact.Snapshot.Identity.LibraryRoot,
            [delta],
            projected,
            compactIfThresholdReached: false,
            intent.IntentId,
            completeMutationIntent: true,
            CancellationToken.None);
        await store.CompactAsync(preExact.Snapshot.Identity.LibraryRoot, projected, CancellationToken.None);
        LibraryState ready = projected.AdvanceWorkflow(
            LibraryWorkflowPhase.CandidatePreparationReady,
            projected.ProjectedAtUtc.AddSeconds(1));
        await store.WriteWorkflowCheckpointAsync(
            preExact.Snapshot.Identity.LibraryRoot, ready, CancellationToken.None);

        PostExactRefreshBasis? basis = await new VersionedJsonLibraryStateStore(
                new() { StorageRoot = cache })
            .ReadPostExactRefreshBasisAsync(preExact.Snapshot.Identity.LibraryRoot, CancellationToken.None);

        basis.Should().NotBeNull();
        basis!.PreExactState.Snapshot.Should().BeEquivalentTo(preExact.Snapshot);
        basis.PreExactState.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.ExactReady);
        basis.CompletedExactDeltas.Should().ContainSingle().Which.Should().BeEquivalentTo(delta);
        Directory.GetFiles(cache, "*.baseline.json").Should().ContainSingle();
        Directory.GetFiles(cache, "*.checkpoint.json").Should().ContainSingle();
        Directory.GetFiles(cache, "*.deltas.jsonl").Should().HaveCount(2);
    }

    [Fact]
    public async Task NothingToDoExactCompletionProvidesEmptyRefreshBasis()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState preExact = LibraryState.FromScan(fixture.Snapshot,
                new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt);
        await store.WriteBaselineAsync(preExact, CancellationToken.None);
        LibraryState ready = preExact.AdvanceWorkflow(
            LibraryWorkflowPhase.CandidatePreparationReady,
            preExact.ProjectedAtUtc);
        await store.WriteWorkflowCheckpointAsync(
            preExact.Snapshot.Identity.LibraryRoot, ready, CancellationToken.None);

        PostExactRefreshBasis? basis = await store.ReadPostExactRefreshBasisAsync(
            preExact.Snapshot.Identity.LibraryRoot, CancellationToken.None);

        basis.Should().NotBeNull();
        basis!.PreExactState.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.ExactReady);
        basis.CompletedExactDeltas.Should().BeEmpty();
    }

    [Fact]
    public async Task ExactOnlyBaselineCarriesForwardReusableAssessmentsFromPriorGeneration()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        CalibreBook sourceBook = fixture.Snapshot.Books.First(value => value.Formats.Count > 0);
        FormatFileFingerprint fingerprint = sourceBook.Formats[0].Fingerprint!;
        EpubAssessment assessment = new EpubAssessmentEngine().Assess(
            sourceBook.Id,
            sourceBook.Formats[0].ExpectedRelativePath,
            fingerprint,
            EpubInspectionResult.Failed(
                sourceBook.Id,
                sourceBook.Formats[0].ExpectedRelativePath,
                EpubInspectionProblemCode.CannotOpen,
                "Synthetic."));
        LibrarySnapshot assessedSnapshot = new(
            fixture.Snapshot.Identity,
            fixture.Snapshot.ScannedAt,
            fixture.Snapshot.Books,
            fixture.Snapshot.Findings,
            epubAssessments: [assessment]);
        LibraryState assessed = LibraryState.FromScan(
            assessedSnapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        await store.WriteBaselineAsync(assessed, CancellationToken.None);
        LibraryState exactOnly = LibraryState.FromScan(
                fixture.Snapshot,
                new(Guid.Parse("11111111-2222-3333-4444-555555555555")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt);
        await store.WriteBaselineAsync(exactOnly, CancellationToken.None);

        LibrarySnapshot? reusable = await new VersionedJsonLibraryStateStore(
                new() { StorageRoot = cache })
            .ReadReusableAssessmentSnapshotAsync(fixture.Snapshot.Identity.LibraryRoot, CancellationToken.None);

        reusable.Should().NotBeNull();
        reusable!.EpubAssessments.Should().ContainSingle().Which.Should().BeEquivalentTo(assessment);
        Directory.GetFiles(cache, "*.reusable-assessments.json").Should().ContainSingle();
    }

    [Fact]
    public async Task PostExactRefreshBaselineReplaysExactSourceProvenance()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryWorkflowSource source = new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            new(42));
        LibraryStateGenerationId refreshedGeneration = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"));
        LibraryState refreshed = new(
            refreshedGeneration,
            new(0),
            LibraryStateStatus.Authoritative,
            fixture.Snapshot,
            fixture.Snapshot.ScannedAt,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.CandidatePreparationReady,
                refreshedGeneration,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                fixture.Snapshot.ScannedAt,
                source));
        await store.WriteBaselineAsync(refreshed, CancellationToken.None);

        LibraryState? loaded = await new VersionedJsonLibraryStateStore(new() { StorageRoot = cache })
            .ReadAsync(fixture.Snapshot.Identity.LibraryRoot, CancellationToken.None);

        loaded!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidatePreparationReady);
        loaded.WorkflowCheckpoint.Source.Should().Be(source);
        loaded.IsWorkflowCheckpointCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task CandidateAnalysisBaselineReplaysUnifiedGroupsAndExactSourceProvenance()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryWorkflowSource source = new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            new(42));
        LibraryStateGenerationId generation = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"));
        CalibreBook[] books = fixture.Snapshot.Books.Take(2).Select(value => new CalibreBook(
            value.Id,
            "Shared title",
            "Shared Author",
            [new(value.Authors[0].Id, "Shared Author", "Shared Author")],
            value.Identifiers,
            value.Formats,
            value.RelativeDirectory,
            value.PublicationMetadata)).ToArray();
        LibrarySnapshot refreshedSnapshot = new(
            fixture.Snapshot.Identity, fixture.Snapshot.ScannedAt, books, []);
        LibraryState refreshed = new(
            generation,
            new(0),
            LibraryStateStatus.Authoritative,
            refreshedSnapshot,
            refreshedSnapshot.ScannedAt,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.CandidatePreparationReady,
                generation,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                refreshedSnapshot.ScannedAt,
                source));
        await store.WriteBaselineAsync(refreshed, CancellationToken.None);
        ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect(books).Single();
        UnifiedCandidateGroup unified = UnifiedCandidateMergePolicy.Merge([metadata], [], books).Single();
        LibrarySnapshot analyzedSnapshot = new(
            refreshedSnapshot.Identity,
            refreshedSnapshot.ScannedAt.AddSeconds(1),
            books,
            [],
            exactMetadataDuplicateGroups: [metadata],
            unifiedCandidateGroups: [unified]);
        LibraryState analyzed = new(
            generation,
            new(0),
            LibraryStateStatus.Authoritative,
            analyzedSnapshot,
            analyzedSnapshot.ScannedAt,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.CandidateAnalysisReady,
                generation,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                analyzedSnapshot.ScannedAt,
                source));

        await store.WriteCandidateAnalysisAsync(
            analyzedSnapshot.Identity.LibraryRoot, analyzed, source, CancellationToken.None);
        LibraryState? loaded = await new VersionedJsonLibraryStateStore(new() { StorageRoot = cache })
            .ReadAsync(analyzedSnapshot.Identity.LibraryRoot, CancellationToken.None);

        loaded!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidateAnalysisReady);
        loaded.WorkflowCheckpoint.Source.Should().Be(source);
        loaded.Snapshot.UnifiedCandidateGroups.Should().ContainSingle().Which
            .Should().BeEquivalentTo(unified);
    }

    [Fact]
    public async Task PostExactRefreshBaselineRejectsChangedPersistedSource()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState exact = LibraryState.FromScan(
                fixture.Snapshot,
                new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt)
            .AdvanceWorkflow(LibraryWorkflowPhase.CandidatePreparationReady, fixture.Snapshot.ScannedAt);
        await store.WriteBaselineAsync(exact, CancellationToken.None);
        LibraryWorkflowSource staleSource = new(exact.GenerationId, exact.Revision);
        LibraryState replacement = LibraryState.FromScan(
                fixture.Snapshot,
                new(Guid.Parse("11111111-2222-3333-4444-555555555555")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt);
        await store.WriteBaselineAsync(replacement, CancellationToken.None);
        LibraryStateGenerationId refreshedGeneration = new(
            Guid.Parse("99999999-8888-7777-6666-555555555555"));
        LibraryState refreshed = new(
            refreshedGeneration,
            new(0),
            LibraryStateStatus.Authoritative,
            fixture.Snapshot,
            fixture.Snapshot.ScannedAt,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.CandidatePreparationReady,
                refreshedGeneration,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                fixture.Snapshot.ScannedAt,
                staleSource));

        Func<Task> act = () => store.WritePostExactRefreshBaselineAsync(
            refreshed, staleSource, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        LibraryState? loaded = await store.ReadAsync(
            fixture.Snapshot.Identity.LibraryRoot, CancellationToken.None);
        loaded!.GenerationId.Should().Be(replacement.GenerationId);
    }

    [Fact]
    public async Task MissingWorkflowCheckpointMigratesConservatively()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState state = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt);
        await store.WriteBaselineAsync(state, CancellationToken.None);
        string manifestPath = Directory.GetFiles(cache, "*.library-state.json").Single();
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest.Remove("workflowCheckpoint");
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        LibraryState? loaded = await store.ReadAsync(fixture.Snapshot.Identity.LibraryRoot,
            CancellationToken.None);

        loaded!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.RequiresExactAnalysis);
        loaded.WorkflowCheckpoint.Revision.Should().Be(loaded.Revision);
        loaded.IsWorkflowCheckpointCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task IncompatibleExactAnalysisPolicyMigratesConservatively()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState state = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, fixture.Snapshot.ScannedAt);
        await store.WriteBaselineAsync(state, CancellationToken.None);
        string manifestPath = Directory.GetFiles(cache, "*.library-state.json").Single();
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["workflowCheckpoint"]!["exactAnalysisPolicyVersion"] = "exact-analysis/1.0.0";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        LibraryState? loaded = await store.ReadAsync(fixture.Snapshot.Identity.LibraryRoot,
            CancellationToken.None);

        loaded!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.RequiresExactAnalysis);
        loaded.WorkflowCheckpoint.PolicyVersions.Should().Be(LibraryWorkflowPolicyVersions.Current);
        loaded.IsWorkflowCheckpointCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task BaselineAndDeltasReplayAcrossStoreInstances()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore writer = new(new() { StorageRoot = cache });
        LibraryState state = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        CalibreBook removable = state.Snapshot.Books.First(value => value.Formats.Count > 0);
        BookFormat format = removable.Formats[0];

        await writer.WriteBaselineAsync(state, CancellationToken.None);
        RemoveFormatLibraryStateDelta removeFormat = new(state.GenerationId, state.Revision,
            $"remove-format:{removable.Id.Value}:{format.Format}", state.ProjectedAtUtc.AddSeconds(1),
            removable.Id, format.Format, format.Fingerprint!);
        state = LibraryStateDeltaPolicy.Apply(state, removeFormat);
        await writer.AppendDeltaAsync(fixture.Snapshot.Identity.LibraryRoot, removeFormat, state,
            CancellationToken.None);
        if (state.Snapshot.Books.Single(value => value.Id == removable.Id).Formats.Count == 0)
        {
            RemoveRecordLibraryStateDelta removeRecord = new(state.GenerationId, state.Revision,
                $"remove-record:{removable.Id.Value}", state.ProjectedAtUtc.AddSeconds(1), removable.Id);
            state = LibraryStateDeltaPolicy.Apply(state, removeRecord);
            await writer.AppendDeltaAsync(fixture.Snapshot.Identity.LibraryRoot, removeRecord, state,
                CancellationToken.None);
        }

        VersionedJsonLibraryStateStore reader = new(new() { StorageRoot = cache });
        LibraryState? loaded = await reader.ReadAsync(fixture.Snapshot.Identity.LibraryRoot,
            CancellationToken.None);

        loaded.Should().BeEquivalentTo(state);
        Directory.GetFiles(cache, "*.baseline.json").Should().ContainSingle();
        Directory.GetFiles(cache, "*.deltas.jsonl").Should().ContainSingle();
        Directory.GetFiles(cache, "*.library-state.json").Should().ContainSingle();
    }

    [Fact]
    public async Task DeltaBatchReplaysEveryLogicalTransition()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new()
        {
            StorageRoot = cache,
            StateDeltaCompactionThreshold = 2,
        });
        CalibreBook[] books = fixture.Snapshot.Books.Concat([
            new(new(9001), "Empty 1", "Author", [new(new(9001), "Author", "Author")], [], [], "Empty 1"),
            new(new(9002), "Empty 2", "Author", [new(new(9002), "Author", "Author")], [], [], "Empty 2"),
        ]).ToArray();
        LibrarySnapshot expanded = new(fixture.Snapshot.Identity, fixture.Snapshot.ScannedAt, books,
            fixture.Snapshot.Findings, fixture.Snapshot.ExactBinaryDuplicateGroups,
            fixture.Snapshot.ExactMetadataDuplicateGroups, fixture.Snapshot.EpubAssessments,
            fixture.Snapshot.ConsolidationRecommendations, fixture.Snapshot.PdfAssessments);
        LibraryState state = LibraryState.FromScan(expanded,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        await store.WriteBaselineAsync(state, CancellationToken.None);
        LibraryStateDelta[] deltas =
        [
            new RemoveRecordLibraryStateDelta(state.GenerationId, state.Revision,
                "remove:9001", state.ProjectedAtUtc.AddSeconds(1), new(9001)),
            new RemoveRecordLibraryStateDelta(state.GenerationId, state.Revision.Next(),
                "remove:9002", state.ProjectedAtUtc.AddSeconds(1), new(9002)),
        ];
        LibraryStateMutationIntent intent = new("chunk-1", state.GenerationId, state.Revision,
            deltas.Length, state.ProjectedAtUtc.AddSeconds(1));
        await store.WriteMutationIntentAsync(expanded.Identity.LibraryRoot, intent, CancellationToken.None);
        foreach (LibraryStateDelta delta in deltas) state = LibraryStateDeltaPolicy.Apply(state, delta);

        await store.AppendDeltaBatchAsync(expanded.Identity.LibraryRoot, deltas, state,
            compactIfThresholdReached: false, intent.IntentId, completeMutationIntent: true,
            CancellationToken.None);
        LibraryState? committed = await new VersionedJsonLibraryStateStore(new() { StorageRoot = cache })
            .ReadAsync(expanded.Identity.LibraryRoot, CancellationToken.None);
        committed!.Status.Should().Be(LibraryStateStatus.Authoritative);
        Directory.GetFiles(cache, "*.checkpoint.json").Should().BeEmpty();
        await store.CompactAsync(expanded.Identity.LibraryRoot, state, CancellationToken.None);
        LibraryState? loaded = await new VersionedJsonLibraryStateStore(new() { StorageRoot = cache })
            .ReadAsync(expanded.Identity.LibraryRoot, CancellationToken.None);

        loaded.Should().BeEquivalentTo(state);
        Directory.GetFiles(cache, "*.checkpoint.json").Should().ContainSingle();
        File.ReadAllText(Directory.GetFiles(cache, "*.deltas.jsonl").Single()).Should().BeEmpty();
    }

    [Fact]
    public async Task UnmatchedMutationIntentLoadsAsUncertain()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState state = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        CalibreBook book = state.Snapshot.Books.First(value => value.Formats.Count > 0);
        BookFormat format = book.Formats[0];
        LibraryStateMutationIntent intent = new("chunk-1", state.GenerationId, state.Revision,
            1, state.ProjectedAtUtc.AddSeconds(1));
        await store.WriteBaselineAsync(state, CancellationToken.None);

        await store.WriteMutationIntentAsync(state.Snapshot.Identity.LibraryRoot, intent, CancellationToken.None);
        LibraryState? loaded = await new VersionedJsonLibraryStateStore(new() { StorageRoot = cache })
            .ReadAsync(state.Snapshot.Identity.LibraryRoot, CancellationToken.None);

        loaded!.Status.Should().Be(LibraryStateStatus.Uncertain);
        loaded.Uncertainty!.Code.Should().Be("MUTATION_INTENT_INCOMPLETE");
        loaded.Uncertainty.OperationId.Should().Be("chunk-1");
    }

    [Fact]
    public async Task PartiallyCommittedMutationIntentReplaysPrefixAsUncertain()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        CalibreBook[] books = fixture.Snapshot.Books.Concat([
            new(new(9001), "Empty 1", "Author", [new(new(9001), "Author", "Author")], [], [], "Empty 1"),
            new(new(9002), "Empty 2", "Author", [new(new(9002), "Author", "Author")], [], [], "Empty 2"),
        ]).ToArray();
        LibrarySnapshot expanded = new(fixture.Snapshot.Identity, fixture.Snapshot.ScannedAt, books,
            fixture.Snapshot.Findings, fixture.Snapshot.ExactBinaryDuplicateGroups,
            fixture.Snapshot.ExactMetadataDuplicateGroups, fixture.Snapshot.EpubAssessments,
            fixture.Snapshot.ConsolidationRecommendations, fixture.Snapshot.PdfAssessments);
        LibraryState state = LibraryState.FromScan(expanded,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        string[] operationIds = ["remove:9001", "remove:9002"];
        LibraryStateMutationIntent intent = new("chunk-1", state.GenerationId, state.Revision,
            operationIds.Length, state.ProjectedAtUtc.AddSeconds(1));
        await store.WriteBaselineAsync(state, CancellationToken.None);
        await store.WriteMutationIntentAsync(expanded.Identity.LibraryRoot, intent, CancellationToken.None);
        RemoveRecordLibraryStateDelta delta = new(state.GenerationId, state.Revision,
            operationIds[0], state.ProjectedAtUtc.AddSeconds(1), new(9001));
        state = LibraryStateDeltaPolicy.Apply(state, delta);

        await store.AppendDeltaBatchAsync(expanded.Identity.LibraryRoot, [delta], state,
            compactIfThresholdReached: false, intent.IntentId, completeMutationIntent: false,
            CancellationToken.None);
        LibraryState? loaded = await new VersionedJsonLibraryStateStore(new() { StorageRoot = cache })
            .ReadAsync(expanded.Identity.LibraryRoot, CancellationToken.None);

        loaded!.Status.Should().Be(LibraryStateStatus.Uncertain);
        loaded.Revision.Value.Should().Be(1);
        loaded.Snapshot.Books.Should().NotContain(value => value.Id == new CalibreBookId(9001));
        loaded.Snapshot.Books.Should().Contain(value => value.Id == new CalibreBookId(9002));
        loaded.Uncertainty!.Code.Should().Be("MUTATION_INTENT_INCOMPLETE");
    }

    [Fact]
    public async Task TamperedDeltaJournalLoadsAsUncertain()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new() { StorageRoot = cache });
        LibraryState baseline = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        CalibreBook removable = baseline.Snapshot.Books.First(value => value.Formats.Count > 0);
        BookFormat format = removable.Formats[0];
        await store.WriteBaselineAsync(baseline, CancellationToken.None);
        RemoveFormatLibraryStateDelta delta = new(baseline.GenerationId, baseline.Revision,
            "remove-format", baseline.ProjectedAtUtc.AddSeconds(1), removable.Id, format.Format,
            format.Fingerprint!);
        LibraryState projected = LibraryStateDeltaPolicy.Apply(baseline, delta);
        await store.AppendDeltaAsync(fixture.Snapshot.Identity.LibraryRoot, delta, projected,
            CancellationToken.None);
        string journal = Directory.GetFiles(cache, "*.deltas.jsonl").Single();
        string content = await File.ReadAllTextAsync(journal);
        await File.WriteAllTextAsync(journal, content.Replace("remove-format", "remove-fxrmat",
            StringComparison.Ordinal));

        LibraryState? loaded = await store.ReadAsync(fixture.Snapshot.Identity.LibraryRoot,
            CancellationToken.None);

        loaded!.Status.Should().Be(LibraryStateStatus.Uncertain);
        loaded.Uncertainty!.Code.Should().Be("STATE_REPLAY_FAILED");
    }

    [Fact]
    public async Task DeltaThresholdCompactsToEquivalentCheckpoint()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cache = Path.Combine(directory.Path, "state-cache");
        VersionedJsonLibraryStateStore store = new(new()
        {
            StorageRoot = cache,
            StateDeltaCompactionThreshold = 2,
        });
        LibraryState state = LibraryState.FromScan(fixture.Snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        CalibreBook[] removable = state.Snapshot.Books.Where(value => value.Formats.Count == 0)
            .Take(2).ToArray();
        if (removable.Length < 2)
        {
            CalibreBook[] books = state.Snapshot.Books.Concat([
                new(new(9001), "Empty 1", "Author", [new(new(9001), "Author", "Author")], [], [], "Empty 1"),
                new(new(9002), "Empty 2", "Author", [new(new(9002), "Author", "Author")], [], [], "Empty 2"),
            ]).ToArray();
            LibrarySnapshot expanded = new(state.Snapshot.Identity, state.Snapshot.ScannedAt, books,
                state.Snapshot.Findings, state.Snapshot.ExactBinaryDuplicateGroups,
                state.Snapshot.ExactMetadataDuplicateGroups, state.Snapshot.EpubAssessments,
                state.Snapshot.ConsolidationRecommendations, state.Snapshot.PdfAssessments);
            state = LibraryState.FromScan(expanded, state.GenerationId);
            removable = books.Where(value => value.Id.Value >= 9001).ToArray();
        }
        await store.WriteBaselineAsync(state, CancellationToken.None);
        foreach (CalibreBook book in removable.Take(2))
        {
            RemoveRecordLibraryStateDelta delta = new(state.GenerationId, state.Revision,
                $"remove:{book.Id.Value}", state.ProjectedAtUtc.AddSeconds(1), book.Id);
            state = LibraryStateDeltaPolicy.Apply(state, delta);
            await store.AppendDeltaAsync(state.Snapshot.Identity.LibraryRoot, delta, state,
                CancellationToken.None);
        }

        LibraryState? loaded = await store.ReadAsync(state.Snapshot.Identity.LibraryRoot,
            CancellationToken.None);

        loaded.Should().BeEquivalentTo(state);
        Directory.GetFiles(cache, "*.checkpoint.json").Should().ContainSingle();
        File.ReadAllText(Directory.GetFiles(cache, "*.deltas.jsonl").Single()).Should().BeEmpty();
    }
}
