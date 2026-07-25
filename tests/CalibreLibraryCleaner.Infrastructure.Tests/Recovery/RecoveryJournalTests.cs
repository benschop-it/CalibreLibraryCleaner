using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recovery;

public sealed class RecoveryJournalTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task MutationWithoutTerminalSummaryRemainsManualInterventionRequired()
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "external");
        string bundle = Path.Combine(destination, $"recovery-{Guid.NewGuid():D}");
        Directory.CreateDirectory(bundle);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider(temporary.Path);
        await WritePlanAsync(provider, plan, bundle, temporary.Path);
        (string manifestFile, string manifestInternal) =
            await WriteManifestAsync(bundle, executionId, plan);
        IRecoveryJournalStore store = provider.GetRequiredService<IRecoveryJournalStore>();
        await using (IRecoveryJournalSession journal = await store.CreateAsync(new(
                         executionId, plan, Source(plan, bundle), bundle, "1.0.0",
                         DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("RecoveryCreated",
                RecoveryExecutionState.PreflightValidating, DateTimeOffset.UtcNow,
                "preflight"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupStarted",
                RecoveryExecutionState.BackingUpCurrentState, DateTimeOffset.UtcNow,
                "backup"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupVerified",
                RecoveryExecutionState.CurrentStateBackupVerified, DateTimeOffset.UtcNow,
                "verified",
                CurrentStateManifestFileDigest: manifestFile,
                CurrentStateManifestInternalDigest: manifestInternal),
                CancellationToken.None);
            await journal.AppendAsync(new("MutationStarting",
                RecoveryExecutionState.CurrentStateBackupVerified, DateTimeOffset.UtcNow,
                "mutation marker", MutationStarted: true), CancellationToken.None);
        }

        RecoveryJournalReconciliationResult result = await store.ReconcileAsync(
            destination, plan.Definition.InputIdentity.CurrentLibraryUuid,
            CancellationToken.None);

        result.ManualInterventionRequired.Should().BeTrue();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.UNRESOLVED_JOURNAL");
    }

    [Fact]
    public async Task TerminalRecoveredJournalHasDurableFinalHashAndNoUnresolvedState()
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "external");
        string bundle = Path.Combine(destination, $"recovery-{Guid.NewGuid():D}");
        Directory.CreateDirectory(bundle);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider(temporary.Path);
        await WritePlanAsync(provider, plan, bundle, temporary.Path);
        (string manifestFile, string manifestInternal) =
            await WriteManifestAsync(bundle, executionId, plan);
        IRecoveryJournalStore store = provider.GetRequiredService<IRecoveryJournalStore>();
        string? finalHash;
        await using (IRecoveryJournalSession journal = await store.CreateAsync(new(
                         executionId, plan, Source(plan, bundle), bundle, "1.0.0",
                         DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("RecoveryCreated",
                RecoveryExecutionState.PreflightValidating, DateTimeOffset.UtcNow,
                "preflight"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupStarted",
                RecoveryExecutionState.BackingUpCurrentState, DateTimeOffset.UtcNow,
                "backup"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupVerified",
                RecoveryExecutionState.CurrentStateBackupVerified, DateTimeOffset.UtcNow,
                "verified",
                CurrentStateManifestFileDigest: manifestFile,
                CurrentStateManifestInternalDigest: manifestInternal),
                CancellationToken.None);
            await journal.AppendAsync(new("IntermediateVerificationStarted",
                RecoveryExecutionState.VerifyingConstructiveState,
                DateTimeOffset.UtcNow, "intermediate"), CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationStarted",
                RecoveryExecutionState.FinalVerifying, DateTimeOffset.UtcNow,
                "verify"), CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationPassed",
                RecoveryExecutionState.Recovered, DateTimeOffset.UtcNow,
                "verified"), CancellationToken.None);
            await journal.CompleteAsync(new(executionId, plan.Id, plan.ContentDigest,
                plan.Definition.InputIdentity.SourceExecutionId,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                RecoveryExecutionState.Recovered, RecoveryFailureClassification.None,
                bundle, journal.JournalIdentity, manifestInternal,
                DateTimeOffset.UtcNow, false, false, false, false,
                manifestFile,
                plan.Definition.InputIdentity.CanonicalRootIdentityDigest.Value,
                plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                CancellationToken.None);
            finalHash = journal.FinalEntryHash;
        }

        finalHash.Should().MatchRegex("^[0-9a-f]{64}$");
        (await store.ReconcileAsync(destination,
            plan.Definition.InputIdentity.CurrentLibraryUuid,
            CancellationToken.None)).ManualInterventionRequired.Should().BeFalse();

        await File.AppendAllTextAsync(Path.Combine(
            bundle, "current-state-backup-manifest.json"), " ");
        RecoveryJournalReconciliationResult changedBackup =
            await store.ReconcileAsync(destination,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                CancellationToken.None);
        changedBackup.ManualInterventionRequired.Should().BeTrue();
        changedBackup.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.CORRUPT_JOURNAL");
    }

    [Fact]
    public async Task TerminalSummaryMismatchRemainsUnresolved()
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "external");
        string bundle = Path.Combine(destination, $"recovery-{Guid.NewGuid():D}");
        Directory.CreateDirectory(bundle);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider(temporary.Path);
        await WritePlanAsync(provider, plan, bundle, temporary.Path);
        (string manifestFile, string manifestInternal) =
            await WriteManifestAsync(bundle, executionId, plan);
        IRecoveryJournalStore store = provider.GetRequiredService<IRecoveryJournalStore>();
        await using (IRecoveryJournalSession journal = await store.CreateAsync(new(
                         executionId, plan, Source(plan, bundle), bundle, "1.0.0",
                         DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("RecoveryCreated",
                RecoveryExecutionState.PreflightValidating, DateTimeOffset.UtcNow,
                "preflight"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupStarted",
                RecoveryExecutionState.BackingUpCurrentState, DateTimeOffset.UtcNow,
                "backup"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupVerified",
                RecoveryExecutionState.CurrentStateBackupVerified, DateTimeOffset.UtcNow,
                "verified",
                CurrentStateManifestFileDigest: manifestFile,
                CurrentStateManifestInternalDigest: manifestInternal),
                CancellationToken.None);
            await journal.AppendAsync(new("IntermediateVerificationStarted",
                RecoveryExecutionState.VerifyingConstructiveState,
                DateTimeOffset.UtcNow, "intermediate"), CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationStarted",
                RecoveryExecutionState.FinalVerifying, DateTimeOffset.UtcNow,
                "verify"), CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationPassed",
                RecoveryExecutionState.Recovered, DateTimeOffset.UtcNow,
                "verified"), CancellationToken.None);
            await journal.CompleteAsync(new(executionId, plan.Id, plan.ContentDigest,
                plan.Definition.InputIdentity.SourceExecutionId,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                RecoveryExecutionState.Recovered, RecoveryFailureClassification.None,
                bundle, journal.JournalIdentity, manifestInternal,
                DateTimeOffset.UtcNow, false, false, false, false,
                manifestFile,
                plan.Definition.InputIdentity.CanonicalRootIdentityDigest.Value,
                plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                CancellationToken.None);
        }
        await File.AppendAllTextAsync(
            Path.Combine(bundle, "recovery-summary.json"), "tampered");

        RecoveryJournalReconciliationResult result = await store.ReconcileAsync(
            destination, plan.Definition.InputIdentity.CurrentLibraryUuid,
            CancellationToken.None);

        result.ManualInterventionRequired.Should().BeTrue();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.UNRESOLVED_JOURNAL");
    }

    [Fact]
    public async Task DurableProofFailureAfterFinalPassIsManualNotCorrupt()
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "external");
        string bundle = Path.Combine(destination, $"recovery-{Guid.NewGuid():D}");
        Directory.CreateDirectory(bundle);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider(temporary.Path);
        await WritePlanAsync(provider, plan, bundle, temporary.Path);
        (string manifestFile, string manifestInternal) =
            await WriteManifestAsync(bundle, executionId, plan);
        IRecoveryJournalStore store =
            provider.GetRequiredService<IRecoveryJournalStore>();

        await using (IRecoveryJournalSession journal = await store.CreateAsync(
                         new(executionId, plan, Source(plan, bundle), bundle,
                             "1.0.0", DateTimeOffset.UtcNow),
                         CancellationToken.None))
        {
            await journal.AppendAsync(new("RecoveryCreated",
                RecoveryExecutionState.PreflightValidating,
                DateTimeOffset.UtcNow, "preflight"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupStarted",
                RecoveryExecutionState.BackingUpCurrentState,
                DateTimeOffset.UtcNow, "backup"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupVerified",
                RecoveryExecutionState.CurrentStateBackupVerified,
                DateTimeOffset.UtcNow, "verified",
                CurrentStateManifestFileDigest: manifestFile,
                CurrentStateManifestInternalDigest: manifestInternal),
                CancellationToken.None);
            await journal.AppendAsync(new("MutationStarting",
                RecoveryExecutionState.CurrentStateBackupVerified,
                DateTimeOffset.UtcNow, "mutation", MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("IntermediateVerificationStarted",
                RecoveryExecutionState.VerifyingConstructiveState,
                DateTimeOffset.UtcNow, "intermediate",
                MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationStarted",
                RecoveryExecutionState.FinalVerifying,
                DateTimeOffset.UtcNow, "final", MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationPassed",
                RecoveryExecutionState.Recovered,
                DateTimeOffset.UtcNow, "semantic pass",
                MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("RecoveryStopped",
                RecoveryExecutionState.ManualInterventionRequired,
                DateTimeOffset.UtcNow, "durable proof failed",
                FailureCode: "RECOVERY.JOURNAL_WRITE_FAILED",
                MutationStarted: true), CancellationToken.None);
            await journal.CompleteAsync(new(executionId, plan.Id,
                plan.ContentDigest,
                plan.Definition.InputIdentity.SourceExecutionId,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                RecoveryExecutionState.ManualInterventionRequired,
                RecoveryFailureClassification.JournalOrStorage,
                bundle, journal.JournalIdentity, manifestInternal,
                DateTimeOffset.UtcNow, true, false, false, false,
                manifestFile,
                plan.Definition.InputIdentity.CanonicalRootIdentityDigest.Value,
                plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                CancellationToken.None);
        }

        RecoveryJournalReconciliationResult result =
            await store.ReconcileAsync(destination,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                CancellationToken.None);
        result.ManualInterventionRequired.Should().BeTrue();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.UNRESOLVED_JOURNAL");
        result.Issues.Should().NotContain(value =>
            value.Code == "RECOVERY.CORRUPT_JOURNAL");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task ChangedRecordIdRequiresFinalizedSemanticMapping(
        bool finalizeMapping,
        bool tamperFinalizedIdentifiers,
        bool expectedManualIntervention)
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "external");
        string bundle = Path.Combine(destination, $"recovery-{Guid.NewGuid():D}");
        Directory.CreateDirectory(bundle);
        RecoveryPlan plan = PlanWithChangedRecordId();
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider(temporary.Path);
        await WritePlanAsync(provider, plan, bundle, temporary.Path);
        (string manifestFile, string manifestInternal) =
            await WriteManifestAsync(bundle, executionId, plan);
        IRecoveryJournalStore store =
            provider.GetRequiredService<IRecoveryJournalStore>();
        RecoveryOperation operation =
            plan.Definition.OperationGraph.Operations.Single();
        DateTimeOffset mappedAt = DateTimeOffset.UtcNow;
        RecoveryRecordIdMapping initial = new(operation.LogicalRecordId,
            operation.OriginalRecordId, null, new(42), [], [], mappedAt);
        RecoveryRecordIdMapping finalized = new(operation.LogicalRecordId,
            operation.OriginalRecordId, null, new(42), [],
            ["isbn:verified"], mappedAt);
        if (tamperFinalizedIdentifiers)
            finalized = new(operation.LogicalRecordId,
                operation.OriginalRecordId, null, new(42), [],
                ["isbn:substituted"], mappedAt);

        await using (IRecoveryJournalSession journal = await store.CreateAsync(
                         new(executionId, plan, Source(plan, bundle), bundle,
                             "1.0.0", DateTimeOffset.UtcNow),
                         CancellationToken.None))
        {
            await journal.AppendAsync(new("RecoveryCreated",
                RecoveryExecutionState.PreflightValidating,
                DateTimeOffset.UtcNow, "preflight"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupStarted",
                RecoveryExecutionState.BackingUpCurrentState,
                DateTimeOffset.UtcNow, "backup"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupVerified",
                RecoveryExecutionState.CurrentStateBackupVerified,
                DateTimeOffset.UtcNow, "verified",
                CurrentStateManifestFileDigest: manifestFile,
                CurrentStateManifestInternalDigest: manifestInternal),
                CancellationToken.None);
            await journal.AppendAsync(new("MutationStarting",
                RecoveryExecutionState.CurrentStateBackupVerified,
                DateTimeOffset.UtcNow, "mutation", MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("OperationStarting",
                RecoveryExecutionState.RestoringConstructiveState,
                DateTimeOffset.UtcNow, "create", operation.Id,
                MutationStarted: true), CancellationToken.None);
            await journal.AppendAsync(new("CommandFinished",
                RecoveryExecutionState.RestoringConstructiveState,
                DateTimeOffset.UtcNow, "command", operation.Id, "add-empty",
                [], 0, string.Empty, string.Empty, MutationStarted: true),
                CancellationToken.None);
            (await store.ReconcileAsync(destination,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                CancellationToken.None)).Issues.Should().NotContain(value =>
                value.Code == "RECOVERY.CORRUPT_JOURNAL");
            RecoveryJournalEvent mappedEvent = new("RecordIdMapped",
                RecoveryExecutionState.RestoringConstructiveState,
                DateTimeOffset.UtcNow, "mapped", operation.Id,
                MutationStarted: true, RecordIdMapping: initial);
            await journal.AppendAsync(mappedEvent,
                CancellationToken.None);
            (await store.ReconcileAsync(destination,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                CancellationToken.None)).Issues.Should().NotContain(value =>
                value.Code == "RECOVERY.CORRUPT_JOURNAL");
            await journal.AppendAsync(new("OperationVerified",
                RecoveryExecutionState.RestoringConstructiveState,
                DateTimeOffset.UtcNow, "verified", operation.Id,
                MutationStarted: true), CancellationToken.None);
            await journal.AppendAsync(new("IntermediateVerificationStarted",
                RecoveryExecutionState.VerifyingConstructiveState,
                DateTimeOffset.UtcNow, "intermediate",
                MutationStarted: true), CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationStarted",
                RecoveryExecutionState.FinalVerifying,
                DateTimeOffset.UtcNow, "final", MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("FinalVerificationPassed",
                RecoveryExecutionState.Recovered,
                DateTimeOffset.UtcNow, "passed", MutationStarted: true),
                CancellationToken.None);
            if (finalizeMapping)
            {
                await journal.AppendAsync(new("RecordIdMappingFinalized",
                    RecoveryExecutionState.Recovered,
                    DateTimeOffset.UtcNow, "finalized",
                    MutationStarted: true, RecordIdMapping: finalized),
                    CancellationToken.None);
                if (!tamperFinalizedIdentifiers)
                    (await store.ReconcileAsync(destination,
                        plan.Definition.InputIdentity.CurrentLibraryUuid,
                        CancellationToken.None)).Issues.Should().NotContain(value =>
                        value.Code == "RECOVERY.CORRUPT_JOURNAL");
            }
            await journal.CompleteAsync(new(executionId, plan.Id,
                plan.ContentDigest,
                plan.Definition.InputIdentity.SourceExecutionId,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                RecoveryExecutionState.Recovered,
                RecoveryFailureClassification.None, bundle,
                journal.JournalIdentity, manifestInternal,
                DateTimeOffset.UtcNow, true, false, true, false,
                manifestFile,
                plan.Definition.InputIdentity.CanonicalRootIdentityDigest.Value,
                plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                CancellationToken.None);
        }

        RecoveryJournalReconciliationResult result =
            await store.ReconcileAsync(destination,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                CancellationToken.None);
        result.ManualInterventionRequired.Should()
            .Be(expectedManualIntervention, string.Join(" | ",
                result.Issues.Select(value =>
                    $"{value.Code}: {value.Explanation}")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StoppedActiveOperationRequiresDurableOperationIdentity(
        bool includeOperationId,
        bool expectedCorrupt)
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "external");
        string bundle = Path.Combine(destination, $"recovery-{Guid.NewGuid():D}");
        Directory.CreateDirectory(bundle);
        RecoveryPlan plan = PlanWithChangedRecordId();
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider(temporary.Path);
        await WritePlanAsync(provider, plan, bundle, temporary.Path);
        (string manifestFile, string manifestInternal) =
            await WriteManifestAsync(bundle, executionId, plan);
        IRecoveryJournalStore store =
            provider.GetRequiredService<IRecoveryJournalStore>();
        RecoveryOperation operation =
            plan.Definition.OperationGraph.Operations.Single();

        await using (IRecoveryJournalSession journal = await store.CreateAsync(
                         new(executionId, plan, Source(plan, bundle), bundle,
                             "1.0.0", DateTimeOffset.UtcNow),
                         CancellationToken.None))
        {
            await journal.AppendAsync(new("RecoveryCreated",
                RecoveryExecutionState.PreflightValidating,
                DateTimeOffset.UtcNow, "preflight"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupStarted",
                RecoveryExecutionState.BackingUpCurrentState,
                DateTimeOffset.UtcNow, "backup"), CancellationToken.None);
            await journal.AppendAsync(new("CurrentStateBackupVerified",
                RecoveryExecutionState.CurrentStateBackupVerified,
                DateTimeOffset.UtcNow, "verified",
                CurrentStateManifestFileDigest: manifestFile,
                CurrentStateManifestInternalDigest: manifestInternal),
                CancellationToken.None);
            await journal.AppendAsync(new("MutationStarting",
                RecoveryExecutionState.CurrentStateBackupVerified,
                DateTimeOffset.UtcNow, "mutation", MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("OperationStarting",
                RecoveryExecutionState.RestoringConstructiveState,
                DateTimeOffset.UtcNow, "create", operation.Id,
                MutationStarted: true), CancellationToken.None);
            await journal.AppendAsync(new("CommandFinished",
                RecoveryExecutionState.RestoringConstructiveState,
                DateTimeOffset.UtcNow, "command", operation.Id, "add-empty",
                [], 0, string.Empty, string.Empty, MutationStarted: true),
                CancellationToken.None);
            await journal.AppendAsync(new("RecoveryStopped",
                RecoveryExecutionState.PartiallyRecovered,
                DateTimeOffset.UtcNow, "verification failed",
                includeOperationId ? operation.Id : null,
                FailureCode: "RECOVERY.CREATED_RECORD_AMBIGUOUS",
                MutationStarted: true,
                Issues:
                [
                    new("RECOVERY.CREATED_RECORD_AMBIGUOUS",
                        RecoveryIssueSeverity.Blocking,
                        "Created record",
                        "The created record could not be uniquely identified.",
                        operation.LogicalRecordId),
                ]), CancellationToken.None);
            await journal.CompleteAsync(new(executionId, plan.Id,
                plan.ContentDigest,
                plan.Definition.InputIdentity.SourceExecutionId,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                RecoveryExecutionState.PartiallyRecovered,
                RecoveryFailureClassification.ConstructiveVerification,
                bundle, journal.JournalIdentity, manifestInternal,
                DateTimeOffset.UtcNow, true, false, false, false,
                manifestFile,
                plan.Definition.InputIdentity.CanonicalRootIdentityDigest.Value,
                plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                CancellationToken.None);
        }

        RecoveryJournalReconciliationResult result =
            await store.ReconcileAsync(destination,
                plan.Definition.InputIdentity.CurrentLibraryUuid,
                CancellationToken.None);
        result.ManualInterventionRequired.Should().BeTrue();
        result.Issues.Should().Contain(value =>
            value.Code == (expectedCorrupt
                ? "RECOVERY.CORRUPT_JOURNAL"
                : "RECOVERY.UNRESOLVED_JOURNAL"));
    }

    [Fact]
    public async Task MutationMarkerBeforeVerifiedCurrentBackupIsCorrupt()
    {
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "external");
        string bundle = Path.Combine(destination, $"recovery-{Guid.NewGuid():D}");
        Directory.CreateDirectory(bundle);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        using ServiceProvider provider = Provider(temporary.Path);
        await WritePlanAsync(provider, plan, bundle, temporary.Path);
        IRecoveryJournalStore store = provider.GetRequiredService<IRecoveryJournalStore>();
        await using (IRecoveryJournalSession journal = await store.CreateAsync(new(
                         new(Guid.NewGuid()), plan, Source(plan, bundle), bundle,
                         "1.0.0", DateTimeOffset.UtcNow), CancellationToken.None))
        {
            await journal.AppendAsync(new("MutationStarting",
                RecoveryExecutionState.Created, DateTimeOffset.UtcNow,
                "illegal mutation marker", MutationStarted: true),
                CancellationToken.None);
        }

        RecoveryJournalReconciliationResult result = await store.ReconcileAsync(
            destination, plan.Definition.InputIdentity.CurrentLibraryUuid,
            CancellationToken.None);

        result.ManualInterventionRequired.Should().BeTrue();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.CORRUPT_JOURNAL");
    }

    private static RecoverySourceInspection Source(RecoveryPlan plan, string bundle) =>
        new(bundle, null, null, null, null, null, null, null, [], []);

    private static RecoveryPlan PlanWithChangedRecordId()
    {
        RecoveryPlan seed = InfrastructureRecoveryTestData.Plan();
        RecoveryInputIdentity seedInput = seed.Definition.InputIdentity;
        LogicalRecoveryRecordId logical = new("record-mapped");
        ExpectedRecordState expectedRecord = new(new(2), "Recovered",
            "Author", [new(new(1), "Author", "Author")],
            [new("isbn", "verified")], null, null, null, null, [],
            false, "Author/Recovered (2)", []);
        RecoveryRecordIdentity identity = new(logical, new(2), null, null,
            RecoveryRecordFingerprintPolicy.Compute(expectedRecord), null,
            ["isbn:verified"], [], "record:2");
        CurrentStateReconciliation reconciliation =
            CurrentStateReconciliation.Create(
                seedInput.SourceExecutionId.ToString(),
                seedInput.CurrentLibraryUuid,
                seedInput.CurrentLibrarySchemaVersion,
                seedInput.FullStateFingerprint,
                seedInput.AffectedStateFingerprint,
                seedInput.UnrelatedStateFingerprint, [],
                [new(identity,
                    [RecoveryReconciliationClassification.Missing], [])], []);
        RecoveryOperation operation = new(new("construct:create:record-mapped"),
            RecoveryOperationKind.CreateRecoveredRecord,
            RecoveryOperationPhase.Constructive, logical, new(2), null, null,
            null, null, null, null, [],
            new("RECOVERY.VERIFY.CREATE", logical,
                "Created record is uniquely rediscovered."),
            "Create missing record.", "source journal",
            RecoveryRiskLevel.Constructive, [], "CreateEmptyRecord");
        RecoveryPlanId planId = new(Guid.NewGuid());
        RecoveryInputIdentity input = new(seedInput.SourcePlanId,
            seedInput.SourcePlanSchemaVersion, seedInput.SourcePlanRevision,
            seedInput.SourcePlanContentDigest, seedInput.SourceExecutionId,
            seedInput.SourceJournalSchema, seedInput.SourceJournalFileDigest,
            seedInput.SourceJournalFinalEntryHash,
            seedInput.SourceTerminalSummaryDigest,
            seedInput.OriginalManifestSchema,
            seedInput.OriginalManifestInternalDigest,
            seedInput.OriginalManifestFileDigest,
            seedInput.SourceApplicationVersion, seedInput.SourceToolIdentity,
            seedInput.CurrentLibraryUuid,
            seedInput.CurrentLibrarySchemaVersion,
            seedInput.CanonicalRootIdentityDigest,
            reconciliation.FullStateFingerprint,
            reconciliation.AffectedStateFingerprint,
            reconciliation.UnrelatedStateFingerprint,
            reconciliation.Version, reconciliation.Digest,
            seedInput.RecoveryCapabilityProfile);
        RecoveryBackupChain chain = new(input.SourcePlanId,
            input.SourcePlanContentDigest, input.SourceExecutionId,
            input.SourceJournalFileDigest, input.SourceJournalFinalEntryHash,
            input.OriginalManifestFileDigest,
            input.OriginalManifestInternalDigest, planId, null);
        RecoveryPlanDefinition definition = new(input,
            new(input.SourcePlanId, input.SourceExecutionId,
                DateTimeOffset.UnixEpoch,
                CleanupExecutionDisposition.RecoveryRequired.ToString(),
                "C:\\source\\bundle"),
            chain, reconciliation, new([operation]),
            new([new(logical, expectedRecord, null, [])], [], [], [],
                input.UnrelatedStateFingerprint), []);
        RecoveryPlan valid = RecoveryPlanLifecyclePolicy.Create(planId,
            definition, new([], DateTimeOffset.UnixEpoch, input),
            DateTimeOffset.UnixEpoch);
        return RecoveryPlanLifecyclePolicy.Approve(valid, [],
            DateTimeOffset.UnixEpoch.AddMinutes(1));
    }

    private static async Task WritePlanAsync(
        ServiceProvider provider,
        RecoveryPlan plan,
        string bundle,
        string root)
    {
        string library = Path.Combine(root, "library");
        Directory.CreateDirectory(library);
        RecoveryPlanStoreResult result = await provider
            .GetRequiredService<IRecoveryPlanStore>().WriteCreateNewAsync(
                plan, Path.Combine(bundle, "approved.recovery-plan.json"),
                library, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
    }

    private static async Task<(string FileDigest, string InternalDigest)>
        WriteManifestAsync(
            string bundle,
            RecoveryExecutionId executionId,
            RecoveryPlan plan)
    {
        const string schema = "cleanup-recovery-current-state-backup/1.0";
        string sourceFingerprint =
            plan.Definition.InputIdentity.FullStateFingerprint.Value;
        StringBuilder canonical = new();
        foreach (string value in new[]
                 {
                     schema,
                     executionId.ToString(),
                     plan.Id.ToString(),
                     plan.ContentDigest.Value,
                     plan.Definition.InputIdentity.CurrentLibraryUuid,
                     sourceFingerprint,
                 })
            canonical.Append(value.Length).Append(':').Append(value).Append(';');
        string internalDigest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = schema,
            RecoveryExecutionId = executionId.ToString(),
            RecoveryPlanId = plan.Id.ToString(),
            RecoveryPlanContentDigest = plan.ContentDigest.Value,
            LibraryUuid = plan.Definition.InputIdentity.CurrentLibraryUuid,
            SourceStateFingerprint = sourceFingerprint,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ManifestInternalDigest = internalDigest,
            Entries = Array.Empty<object>(),
        }, ManifestJsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(
            bundle, "current-state-backup-manifest.json"), bytes);
        return (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            internalDigest);
    }

    private static ServiceProvider Provider(string root)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new ExecutionStorageOptions
        {
            LeaseRoot = Path.Combine(root, "leases"),
            HistoryRoot = Path.Combine(root, "history"),
        });
        return services.BuildServiceProvider();
    }
}
