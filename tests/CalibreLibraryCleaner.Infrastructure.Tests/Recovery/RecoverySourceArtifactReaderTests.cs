using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recovery;

public sealed class RecoverySourceArtifactReaderTests
{
    [Fact]
    public async Task CompleteMilestone7BundleReverifiesPlanJournalManifestAndEveryEntry()
    {
        using SourceBundleHarness harness = await SourceBundleHarness.CreateAsync();

        RecoverySourceInspection result = await harness.Provider
            .GetRequiredService<IRecoverySourceArtifactReader>()
            .ReadAndVerifyAsync(harness.Workspace.BundlePath, CancellationToken.None);

        result.IsVerified.Should().BeTrue();
        result.Journal!.HasKnownDurableState.Should().BeTrue();
        result.Journal.Operations.Should().OnlyContain(value =>
            value.State == DurableSourceOperationState.DurablyCompleted
                || value.State == DurableSourceOperationState.SatisfiedNoOp);
        result.OriginalBackupManifest!.ManifestDigest.Should().Be(
            harness.Manifest.ManifestDigest);
        result.Artifacts.Should().Contain(value =>
            value.Kind == RecoverySourceArtifactKind.OriginalRawFormat);
    }

    [Fact]
    public async Task ChangedRequiredOriginalBackupItemFailsClosedWithoutModifyingBundle()
    {
        using SourceBundleHarness harness = await SourceBundleHarness.CreateAsync();
        string raw = harness.Manifest.Entries.Single(value =>
            value.Kind == BackupArtifactKind.RawFormat).RelativePath;
        string rawPath = Path.Combine(harness.Workspace.BundlePath,
            raw.Replace('/', Path.DirectorySeparatorChar));
        byte[] beforeManifest = await File.ReadAllBytesAsync(Path.Combine(
            harness.Workspace.BundlePath, "backup-manifest.json"));
        await File.WriteAllTextAsync(rawPath, "tampered");

        RecoverySourceInspection result = await harness.Provider
            .GetRequiredService<IRecoverySourceArtifactReader>()
            .ReadAndVerifyAsync(harness.Workspace.BundlePath, CancellationToken.None);

        result.IsVerified.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code.Contains(
            "BACKUP", StringComparison.Ordinal));
        byte[] afterManifest = await File.ReadAllBytesAsync(Path.Combine(
            harness.Workspace.BundlePath, "backup-manifest.json"));
        afterManifest.Should().Equal(beforeManifest);
    }

    [Fact]
    public async Task NonTerminalSourceJournalIsReconciledWithoutInventingATerminalSummary()
    {
        using SourceBundleHarness harness =
            await SourceBundleHarness.CreateAsync(complete: false);

        RecoverySourceInspection result = await harness.Provider
            .GetRequiredService<IRecoverySourceArtifactReader>()
            .ReadAndVerifyAsync(harness.Workspace.BundlePath, CancellationToken.None);

        result.IsVerified.Should().BeTrue(string.Join(" | ",
            result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        result.TerminalSummary.Should().BeNull();
        result.Journal!.TerminalSummaryDigest.Should().BeNull();
        result.Journal.HasKnownDurableState.Should().BeTrue();
        result.Journal.Operations.Should().Contain(value =>
            value.State == DurableSourceOperationState.CommandOutcomeUncertain);
        File.Exists(Path.Combine(harness.Workspace.BundlePath,
            "execution-summary.json")).Should().BeFalse();
    }

    [Fact]
    public async Task OrphanSummaryWithoutTerminalJournalEventIsAuditOnly()
    {
        using SourceBundleHarness harness =
            await SourceBundleHarness.CreateAsync(complete: false);
        string summaryPath = Path.Combine(harness.Workspace.BundlePath,
            "execution-summary.json");
        await File.WriteAllTextAsync(summaryPath, "{\"incomplete\":true}");

        RecoverySourceInspection result = await harness.Provider
            .GetRequiredService<IRecoverySourceArtifactReader>()
            .ReadAndVerifyAsync(harness.Workspace.BundlePath, CancellationToken.None);

        result.IsVerified.Should().BeTrue(string.Join(" | ",
            result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        result.TerminalSummary.Should().BeNull();
        result.Journal!.TerminalSummaryDigest.Should().BeNull();
        result.Artifacts.Should().Contain(value =>
            value.Kind == RecoverySourceArtifactKind.ExecutionSummary);
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.SOURCE_ORPHAN_SUMMARY_IGNORED");
    }

    private sealed class SourceBundleHarness : IDisposable
    {
        private SourceBundleHarness(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            ExecutionWorkspace workspace,
            VerifiedBackupManifest manifest)
        {
            Temporary = temporary;
            Provider = provider;
            Workspace = workspace;
            Manifest = manifest;
        }

        public TemporaryDirectory Temporary { get; }
        public ServiceProvider Provider { get; }
        public ExecutionWorkspace Workspace { get; }
        public VerifiedBackupManifest Manifest { get; }

        public static async Task<SourceBundleHarness> CreateAsync(
            bool complete = true)
        {
            TemporaryDirectory temporary = new();
            InfrastructureExecutionFixture fixture =
                InfrastructureExecutionTestData.Create(temporary.Path);
            string storage = Path.Combine(temporary.Path, "storage");
            string external = Path.Combine(temporary.Path, "external");
            Directory.CreateDirectory(storage);
            Directory.CreateDirectory(external);
            ServiceCollection services = new();
            services.AddLogging();
            services.AddCalibreLibraryInfrastructure();
            services.AddSingleton(new ExecutionStorageOptions
            {
                LeaseRoot = Path.Combine(storage, "leases"),
                HistoryRoot = Path.Combine(storage, "history"),
            });
            ServiceProvider provider = services.BuildServiceProvider();
            IExecutionBackupStore backups = provider.GetRequiredService<IExecutionBackupStore>();
            CleanupExecutionId executionId =
                new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            ExecutionWorkspace workspace = await backups.CreateWorkspaceAsync(
                executionId, external, CancellationToken.None);
            CalibreToolDescriptor tool = new("C:\\trusted\\calibredb.exe",
                new("C:\\trusted\\calibredb.exe", "9.11.0",
                    new(new string('b', 64)), "calibredb/windows/9.11.0"),
                Enum.GetValues<CalibreExecutionCapability>());
            CleanupExecutionCapabilityResult graph =
                CleanupExecutionCapabilityPolicy.Evaluate(fixture.Plan);
            CleanupExecutionConfirmation confirmation = new(
                fixture.Plan.Id, fixture.Plan.ArtifactRevision,
                fixture.Plan.ContentDigest, fixture.Plan.InputIdentity.LibraryUuid,
                fixture.LibraryRoot, graph.Graph!.Digest, tool.Identity, external,
                DateTimeOffset.UtcNow, true, true);
            IExecutionJournalSession journal = await provider
                .GetRequiredService<IExecutionJournalStore>().CreateAsync(new(
                    workspace, fixture.Plan, fixture.LibraryRoot, "1.0.0",
                    DateTimeOffset.UtcNow), CancellationToken.None);
            ExecutionBackupInputs inputs = await backups.CreateInputsAsync(new(
                workspace, fixture.Plan, confirmation, fixture.LibraryRoot,
                tool, "1.0.0", new(new string('c', 64)),
                DateTimeOffset.UtcNow), CancellationToken.None);
            InfrastructureExecutionTestData.CreateExports(inputs, fixture);
            ExecutionBackupResult backup = await backups.VerifyAndSealAsync(new(
                inputs, fixture.Plan, fixture.LibraryRoot, DateTimeOffset.UtcNow),
                CancellationToken.None);
            backup.IsSuccess.Should().BeTrue();

            await journal.AppendAsync(new("ExecutionCreated",
                CleanupExecutionState.AcquiringLease, DateTimeOffset.UtcNow, "created"),
                CancellationToken.None);
            await journal.AppendAsync(new("PreflightStarted",
                CleanupExecutionState.PreflightValidating, DateTimeOffset.UtcNow, "preflight"),
                CancellationToken.None);
            await journal.AppendAsync(new("PreflightVerified",
                CleanupExecutionState.ReadyForBackup, DateTimeOffset.UtcNow, "ready"),
                CancellationToken.None);
            await journal.AppendAsync(new("BackupStarted",
                CleanupExecutionState.BackingUp, DateTimeOffset.UtcNow, "backup"),
                CancellationToken.None);
            await journal.AppendAsync(new("BackupVerified",
                CleanupExecutionState.BackupVerified, DateTimeOffset.UtcNow, "verified"),
                CancellationToken.None);
            await journal.AppendAsync(new("FinalMutationGate",
                CleanupExecutionState.ReadyToExecute, DateTimeOffset.UtcNow, "gate"),
                CancellationToken.None);
            bool mutationStarted = false;
            foreach (CleanupExecutionOperation operation in graph.Graph.Operations)
            {
                if (operation.Phase == ExecutionOperationPhase.Precondition)
                {
                    await journal.AppendAsync(new("OperationSatisfiedNoOp",
                        CleanupExecutionState.ReadyToExecute, DateTimeOffset.UtcNow,
                        "satisfied", operation.Id), CancellationToken.None);
                    continue;
                }
                if (!mutationStarted)
                {
                    await journal.AppendAsync(new("MutationStarting",
                        CleanupExecutionState.ReadyToExecute, DateTimeOffset.UtcNow,
                        "marker", MutationStarted: true), CancellationToken.None);
                    mutationStarted = true;
                }
                await journal.AppendAsync(new("OperationStarting",
                    CleanupExecutionState.Executing, DateTimeOffset.UtcNow,
                    "starting", operation.Id, MutationStarted: true), CancellationToken.None);
                await journal.AppendAsync(new("CommandFinished",
                    CleanupExecutionState.Executing, DateTimeOffset.UtcNow,
                    "command", operation.Id, ExitCode: 0, MutationStarted: true),
                    CancellationToken.None);
                if (!complete)
                {
                    await journal.DisposeAsync();
                    return new(temporary, provider, workspace, backup.Manifest!);
                }
                await journal.AppendAsync(new("VerificationPassed",
                    CleanupExecutionState.Executing, DateTimeOffset.UtcNow,
                    "semantic verification", operation.Id, MutationStarted: true),
                    CancellationToken.None);
                await journal.AppendAsync(new("OperationVerified",
                    CleanupExecutionState.Executing, DateTimeOffset.UtcNow,
                    "verified", operation.Id, MutationStarted: true), CancellationToken.None);
            }
            await journal.AppendAsync(new("VerificationPassed",
                CleanupExecutionState.Verifying, DateTimeOffset.UtcNow,
                "final verification", MutationStarted: true), CancellationToken.None);
            await journal.CompleteAsync(new(executionId, fixture.Plan.Id,
                fixture.Plan.ContentDigest, fixture.Plan.InputIdentity.LibraryUuid,
                CleanupExecutionState.Completed, CleanupExecutionDisposition.Completed,
                CleanupExecutionFailureClassification.None, workspace.BundlePath,
                journal.JournalIdentity, backup.Manifest!.ManifestDigest.Value,
                DateTimeOffset.UtcNow, true), CancellationToken.None);
            await journal.DisposeAsync();
            return new(temporary, provider, workspace, backup.Manifest);
        }

        public void Dispose()
        {
            Provider.Dispose();
            Temporary.Dispose();
        }
    }
}
