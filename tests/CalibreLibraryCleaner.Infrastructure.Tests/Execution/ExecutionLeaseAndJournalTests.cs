using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

public sealed class ExecutionLeaseAndJournalTests
{
    [Fact]
    public async Task LeaseExcludesConcurrentExecutionAndStaleFileDoesNotClaimOwnership()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        using ServiceProvider provider = Provider(temporary.Path);
        ICleanupExecutionLease lease = provider.GetRequiredService<ICleanupExecutionLease>();
        ExecutionLeaseRequest firstRequest = new(new(Guid.NewGuid()), fixture.LibraryRoot,
            fixture.Plan.InputIdentity.LibraryUuid, DateTimeOffset.UtcNow);
        ExecutionLeaseRequest secondRequest = firstRequest with { ExecutionId = new(Guid.NewGuid()) };

        ExecutionLeaseAcquisition first = await lease.TryAcquireAsync(firstRequest, CancellationToken.None);
        ExecutionLeaseAcquisition concurrent = await lease.TryAcquireAsync(secondRequest, CancellationToken.None);
        await first.Lease!.DisposeAsync();
        ExecutionLeaseAcquisition afterRelease = await lease.TryAcquireAsync(secondRequest, CancellationToken.None);

        first.IsAcquired.Should().BeTrue();
        concurrent.IsAcquired.Should().BeFalse();
        concurrent.Issues.Should().Contain(value => value.Code == "EXECUTION.LEASE_HELD");
        afterRelease.IsAcquired.Should().BeTrue();
        await afterRelease.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task LeaseRejectsLinkedLibraryAliasInsteadOfCreatingASecondLeaseIdentity()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        string alias = Path.Combine(temporary.Path, "library-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, fixture.LibraryRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
            return;
        }
        using ServiceProvider provider = Provider(Path.Combine(temporary.Path, "storage"));

        ExecutionLeaseAcquisition result = await provider.GetRequiredService<ICleanupExecutionLease>()
            .TryAcquireAsync(new(new(Guid.NewGuid()), alias, fixture.Plan.InputIdentity.LibraryUuid,
                DateTimeOffset.UtcNow), CancellationToken.None);

        result.IsAcquired.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.LEASE_LIBRARY_INVALID");
    }

    [Fact]
    public async Task IncompleteJournalWithMutationMarkerReconcilesAsRecoveryRequired()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        string backup = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(backup);
        using ServiceProvider provider = Provider(temporary.Path);
        IExecutionJournalStore store = provider.GetRequiredService<IExecutionJournalStore>();
        ExecutionWorkspace workspace = new(new(Guid.NewGuid()),
            Path.Combine(backup, $"execution-{Guid.NewGuid():D}"), backup);
        Directory.CreateDirectory(workspace.BundlePath);
        await using (IExecutionJournalSession journal = await store.CreateAsync(new(workspace, fixture.Plan,
                         fixture.LibraryRoot, "1.0.0", DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("MutationStarting", CleanupExecutionState.ReadyToExecute,
                DateTimeOffset.UtcNow, "marker", MutationStarted: true), CancellationToken.None);
        }

        JournalReconciliationResult result = await store.ReconcileAsync(backup,
            fixture.Plan.InputIdentity.LibraryUuid, CancellationToken.None);

        result.RecoveryRequired.Should().BeTrue();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.INCOMPLETE_MUTATION_JOURNAL");
    }

    [Fact]
    public async Task IncompletePremutationJournalDoesNotAuthorizeRecoveryOrResume()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        string backup = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(backup);
        using ServiceProvider provider = Provider(temporary.Path);
        IExecutionJournalStore store = provider.GetRequiredService<IExecutionJournalStore>();
        ExecutionWorkspace workspace = new(new(Guid.NewGuid()),
            Path.Combine(backup, $"execution-{Guid.NewGuid():D}"), backup);
        Directory.CreateDirectory(workspace.BundlePath);
        await using (IExecutionJournalSession journal = await store.CreateAsync(new(workspace, fixture.Plan,
                         fixture.LibraryRoot, "1.0.0", DateTimeOffset.UtcNow), CancellationToken.None)) { }

        JournalReconciliationResult result = await store.ReconcileAsync(backup,
            fixture.Plan.InputIdentity.LibraryUuid, CancellationToken.None);

        result.RecoveryRequired.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.INCOMPLETE_PREMUTATION_JOURNAL");
    }

    [Fact]
    public async Task CorruptJournalFailsClosedAndHistoryPersistsRecoveryDisposition()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        string backup = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(backup);
        using ServiceProvider provider = Provider(temporary.Path);
        IExecutionJournalStore journals = provider.GetRequiredService<IExecutionJournalStore>();
        IExecutionHistoryStore history = provider.GetRequiredService<IExecutionHistoryStore>();
        CleanupExecutionId executionId = new(Guid.NewGuid());
        ExecutionWorkspace workspace = new(executionId, Path.Combine(backup, $"execution-{executionId}"), backup);
        Directory.CreateDirectory(workspace.BundlePath);
        await using (IExecutionJournalSession journal = await journals.CreateAsync(new(workspace, fixture.Plan,
                         fixture.LibraryRoot, "1.0.0", DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("MutationStarting", CleanupExecutionState.ReadyToExecute,
                DateTimeOffset.UtcNow, "marker", MutationStarted: true), CancellationToken.None);
        }
        await File.AppendAllTextAsync(Path.Combine(workspace.BundlePath, "execution.journal.jsonl"), "not-json\n");
        ExecutionHistoryEntry entry = new(executionId, fixture.Plan.Id, fixture.Plan.ContentDigest,
            fixture.Plan.InputIdentity.LibraryUuid, CleanupExecutionState.RecoveryRequired,
            CleanupExecutionDisposition.RecoveryRequired, CleanupExecutionFailureClassification.CrashOrIndeterminate,
            workspace.BundlePath, Path.Combine(workspace.BundlePath, "execution.journal.jsonl"), null,
            DateTimeOffset.UtcNow, true);
        await history.RecordAsync(entry, fixture.LibraryRoot, CancellationToken.None);

        JournalReconciliationResult reconciled = await journals.ReconcileAsync(backup,
            fixture.Plan.InputIdentity.LibraryUuid, CancellationToken.None);

        reconciled.RecoveryRequired.Should().BeTrue();
        reconciled.Issues.Should().Contain(value => value.Code == "EXECUTION.JOURNAL_CORRUPT");
        (await history.HasRecoveryRequiredAsync(
            fixture.Plan.InputIdentity.LibraryUuid, fixture.LibraryRoot, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task MutationJournalWithoutMatchingImmutableSummaryRequiresRecovery()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        string backup = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(backup);
        using ServiceProvider provider = Provider(temporary.Path);
        IExecutionJournalStore store = provider.GetRequiredService<IExecutionJournalStore>();
        CleanupExecutionId executionId = new(Guid.NewGuid());
        ExecutionWorkspace workspace = new(executionId, Path.Combine(backup, $"execution-{executionId}"), backup);
        Directory.CreateDirectory(workspace.BundlePath);
        await using (IExecutionJournalSession journal = await store.CreateAsync(new(workspace, fixture.Plan,
                         fixture.LibraryRoot, "1.0.0", DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("MutationStarting", CleanupExecutionState.ReadyToExecute,
                DateTimeOffset.UtcNow, "marker", MutationStarted: true), CancellationToken.None);
            await journal.CompleteAsync(new(executionId, fixture.Plan.Id, fixture.Plan.ContentDigest,
                fixture.Plan.InputIdentity.LibraryUuid, CleanupExecutionState.Completed,
                CleanupExecutionDisposition.Completed, CleanupExecutionFailureClassification.None,
                workspace.BundlePath, journal.JournalIdentity, new string('a', 64),
                DateTimeOffset.UtcNow, true), CancellationToken.None);
        }
        File.Delete(Path.Combine(workspace.BundlePath, "execution-summary.json"));

        JournalReconciliationResult result = await store.ReconcileAsync(
            backup, fixture.Plan.InputIdentity.LibraryUuid, CancellationToken.None);

        result.RecoveryRequired.Should().BeTrue();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.INCOMPLETE_MUTATION_JOURNAL");
    }

    [Fact]
    public async Task CompletedMutationJournalWithMatchingImmutableSummaryDoesNotRequireRecovery()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        string backup = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(backup);
        using ServiceProvider provider = Provider(temporary.Path);
        IExecutionJournalStore store = provider.GetRequiredService<IExecutionJournalStore>();
        CleanupExecutionId executionId = new(Guid.NewGuid());
        ExecutionWorkspace workspace = new(executionId, Path.Combine(backup, $"execution-{executionId}"), backup);
        Directory.CreateDirectory(workspace.BundlePath);
        await using (IExecutionJournalSession journal = await store.CreateAsync(new(workspace, fixture.Plan,
                         fixture.LibraryRoot, "1.0.0", DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("MutationStarting", CleanupExecutionState.ReadyToExecute,
                DateTimeOffset.UtcNow, "marker", MutationStarted: true), CancellationToken.None);
            await journal.CompleteAsync(new(executionId, fixture.Plan.Id, fixture.Plan.ContentDigest,
                fixture.Plan.InputIdentity.LibraryUuid, CleanupExecutionState.Completed,
                CleanupExecutionDisposition.Completed, CleanupExecutionFailureClassification.None,
                workspace.BundlePath, journal.JournalIdentity, new string('a', 64),
                DateTimeOffset.UtcNow, true), CancellationToken.None);
        }

        JournalReconciliationResult result = await store.ReconcileAsync(
            backup, fixture.Plan.InputIdentity.LibraryUuid, CancellationToken.None);

        result.RecoveryRequired.Should().BeFalse();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task DurableRecoveryGuardIsAtomicallyReplacedOnlyByCompletedTerminalHistory()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        using ServiceProvider provider = Provider(temporary.Path);
        IExecutionHistoryStore history = provider.GetRequiredService<IExecutionHistoryStore>();
        CleanupExecutionId executionId = new(Guid.NewGuid());
        ExecutionHistoryEntry recovery = new(executionId, fixture.Plan.Id, fixture.Plan.ContentDigest,
            fixture.Plan.InputIdentity.LibraryUuid, CleanupExecutionState.RecoveryRequired,
            CleanupExecutionDisposition.RecoveryRequired, CleanupExecutionFailureClassification.CrashOrIndeterminate,
            "bundle", "journal", new string('a', 64), DateTimeOffset.UtcNow, true);

        await history.RecordAsync(recovery, fixture.LibraryRoot, CancellationToken.None);
        (await history.HasRecoveryRequiredAsync(
            fixture.Plan.InputIdentity.LibraryUuid, fixture.LibraryRoot, CancellationToken.None)).Should().BeTrue();
        await history.RecordAsync(recovery with
        {
            State = CleanupExecutionState.Completed,
            Disposition = CleanupExecutionDisposition.Completed,
            FailureClassification = CleanupExecutionFailureClassification.None,
        }, fixture.LibraryRoot, CancellationToken.None);

        (await history.HasRecoveryRequiredAsync(
            fixture.Plan.InputIdentity.LibraryUuid, fixture.LibraryRoot, CancellationToken.None)).Should().BeFalse();
    }

    private static ServiceProvider Provider(string root)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new ExecutionStorageOptions
        {
            LeaseRoot = Path.Combine(root, "storage", "leases"),
            HistoryRoot = Path.Combine(root, "storage", "history"),
        });
        return services.BuildServiceProvider();
    }
}
