using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recovery;

public sealed class RecoveryHistoryStoreTests
{
    [Fact]
    public async Task RecoveredHistoryWithoutAuthoritativeJournalRemainsUnresolved()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string historyRoot = Path.Combine(temporary.Path, "history");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(historyRoot);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider(temporary.Path, historyRoot);
        IRecoveryHistoryStore store = provider.GetRequiredService<IRecoveryHistoryStore>();
        RecoveryHistoryEntry guard = Entry(plan, executionId,
            RecoveryExecutionState.ManualInterventionRequired,
            RecoveryFailureClassification.CrashOrIndeterminate);
        RecoveryHistoryEntry recovered = Entry(plan, executionId,
            RecoveryExecutionState.Recovered, RecoveryFailureClassification.None);

        await store.RecordAsync(guard, library, CancellationToken.None);
        await store.RecordAsync(recovered, library, CancellationToken.None);

        string executionRoot = Path.Combine(historyRoot, "recovery", "recoveries",
            executionId.ToString());
        Directory.EnumerateFiles(executionRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Should().HaveCount(2);
        IReadOnlyList<RecoveryHistoryEntry> values = await store.ReadAsync(
            plan.Definition.InputIdentity.CurrentLibraryUuid, library,
            CancellationToken.None);
        values.Should().ContainSingle().Which.State.Should().Be(
            RecoveryExecutionState.Recovered);
        (await store.HasUnresolvedRecoveryAsync(
            plan.Definition.InputIdentity.CurrentLibraryUuid, library, null,
            CancellationToken.None)).Should().BeTrue(
            "a secondary history value cannot prove terminal recovery success");
    }

    private static RecoveryHistoryEntry Entry(
        RecoveryPlan plan,
        RecoveryExecutionId executionId,
        RecoveryExecutionState state,
        RecoveryFailureClassification failure) =>
        new(executionId, plan.Id, plan.ContentDigest,
            plan.Definition.InputIdentity.SourceExecutionId,
            plan.Definition.InputIdentity.CurrentLibraryUuid,
            state, failure, "bundle", "journal", new string('a', 64),
            DateTimeOffset.UtcNow, true, state == RecoveryExecutionState.ManualInterventionRequired,
            true, false);

    private static ServiceProvider Provider(string root, string historyRoot)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new ExecutionStorageOptions
        {
            LeaseRoot = Path.Combine(root, "leases"),
            HistoryRoot = historyRoot,
        });
        return services.BuildServiceProvider();
    }
}
