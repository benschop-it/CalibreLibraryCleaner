using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LibrarySnapshots;

public sealed class VersionedJsonLibraryStateStoreTests
{
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
