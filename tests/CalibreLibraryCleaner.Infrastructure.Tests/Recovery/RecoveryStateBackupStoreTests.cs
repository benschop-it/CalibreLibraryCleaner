using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recovery;

public sealed class RecoveryStateBackupStoreTests
{
    [Fact]
    public async Task CurrentStateBackupIncludesVerifiedSourceAuditCopiesAndNeverChangesOriginals()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string external = Path.Combine(temporary.Path, "external");
        string sourceBundle = Path.Combine(temporary.Path, "source");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(sourceBundle);
        await File.WriteAllBytesAsync(Path.Combine(library, "metadata.db"), [0x00]);
        RecoverySourceInspection source = await SourceAsync(sourceBundle);
        Dictionary<string, byte[]> originalBytes = source.Artifacts.ToDictionary(
            value => value.PhysicalIdentity, value => File.ReadAllBytes(value.PhysicalIdentity),
            StringComparer.OrdinalIgnoreCase);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId =
            new(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        using ServiceProvider provider = Provider();
        IRecoveryStateBackupService service =
            provider.GetRequiredService<IRecoveryStateBackupService>();
        string bundle = await service.CreateWorkspaceAsync(
            executionId, external, CancellationToken.None);
        RecoveryCurrentStateSnapshot current = EmptyCurrent(library);

        RecoveryCurrentStateBackupResult result = await service.CreateAndVerifyAsync(new(
            executionId, plan, source, current, library, Tool(),
            external, bundle, "1.0.0", DateTimeOffset.UtcNow,
            Confirmation(plan, current, external)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(string.Join(" | ",
            result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        result.Backup!.Artifacts.Should().Contain(value =>
            value.RelativePath == "source-artifact-audit/source-cleanup-plan.json");
        result.Backup.Artifacts.Should().Contain(value =>
            value.RelativePath == "source-artifact-audit/source-execution-journal.jsonl");
        result.Backup.Artifacts.Should().Contain(value =>
            value.RelativePath == "source-artifact-audit/source-execution-summary.json");
        result.Backup.Artifacts.Should().Contain(value =>
            value.RelativePath == "source-artifact-audit/source-backup-manifest.json");
        foreach ((string path, byte[] bytes) in originalBytes)
            File.ReadAllBytes(path).Should().Equal(bytes);
        (await service.VerifyAvailableAsync(result.Backup, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedCurrentBackupArtifactFailsIndependentReverification()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string external = Path.Combine(temporary.Path, "external");
        string sourceBundle = Path.Combine(temporary.Path, "source");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(sourceBundle);
        await File.WriteAllBytesAsync(Path.Combine(library, "metadata.db"), [0x00]);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider();
        IRecoveryStateBackupService service =
            provider.GetRequiredService<IRecoveryStateBackupService>();
        string bundle = await service.CreateWorkspaceAsync(
            executionId, external, CancellationToken.None);
        RecoveryCurrentStateBackupResult created = await service.CreateAndVerifyAsync(new(
            executionId, plan, await SourceAsync(sourceBundle), EmptyCurrent(library),
            library, Tool(), external, bundle, "1.0.0", DateTimeOffset.UtcNow,
            Confirmation(plan, EmptyCurrent(library), external)),
            CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        RecoveryCurrentStateBackupArtifact artifact = created.Backup!.Artifacts.Single(
            value => value.RelativePath == "source-artifact-audit/source-execution-journal.jsonl");
        await File.AppendAllTextAsync(Path.Combine(bundle,
            artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)), "tampered");

        IReadOnlyList<RecoveryIssue> issues = await service.VerifyAvailableAsync(
            created.Backup, CancellationToken.None);

        issues.Should().Contain(value =>
            value.Code == "RECOVERY.CURRENT_BACKUP_ITEM_CHANGED");
    }

    [Fact]
    public async Task NonTerminalSourceWithoutSummaryCanStillBeBackedUpBeforeRecovery()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string external = Path.Combine(temporary.Path, "external");
        string sourceBundle = Path.Combine(temporary.Path, "source");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(sourceBundle);
        await File.WriteAllBytesAsync(Path.Combine(library, "metadata.db"), [0x00]);
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);
        RecoveryExecutionId executionId = new(Guid.NewGuid());
        using ServiceProvider provider = Provider();
        IRecoveryStateBackupService service =
            provider.GetRequiredService<IRecoveryStateBackupService>();
        string bundle = await service.CreateWorkspaceAsync(
            executionId, external, CancellationToken.None);
        RecoveryCurrentStateSnapshot current = EmptyCurrent(library);

        RecoveryCurrentStateBackupResult result = await service.CreateAndVerifyAsync(new(
            executionId, plan, await SourceAsync(sourceBundle, includeSummary: false),
            current, library, Tool(), external, bundle, "1.0.0",
            DateTimeOffset.UtcNow, Confirmation(plan, current, external)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(string.Join(" | ",
            result.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        result.Backup!.Artifacts.Should().NotContain(value =>
            value.Kind == RecoverySourceArtifactKind.ExecutionSummary);
    }

    private static ServiceProvider Provider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        return services.BuildServiceProvider();
    }

    private static RecoveryCurrentStateSnapshot EmptyCurrent(string libraryRoot)
    {
        LibrarySnapshot snapshot = new(new(
            "44444444-4444-4444-4444-444444444444", 27, libraryRoot),
            DateTimeOffset.UtcNow, [], []);
        return new(snapshot, [], new(new string('1', 64)),
            new(new string('2', 64)), new(new string('3', 64)));
    }

    private static CalibreToolDescriptor Tool()
    {
        ExecutionToolIdentity identity = new("C:\\trusted\\calibredb.exe", "9.11.0",
            new(new string('8', 64)), "calibredb/windows/9.11.0");
        return new(identity.CanonicalExecutableIdentity, identity,
            Enum.GetValues<CalibreExecutionCapability>());
    }

    private static RecoveryExecutionConfirmation Confirmation(
        RecoveryPlan plan,
        RecoveryCurrentStateSnapshot current,
        string destination) =>
        new(plan.Id, plan.ArtifactRevision, plan.ContentDigest,
            plan.Definition.InputIdentity.SourceExecutionId,
            plan.Definition.InputIdentity.CurrentLibraryUuid,
            plan.Definition.InputIdentity.CanonicalRootIdentityDigest,
            current.FullFingerprint,
            plan.Definition.InputIdentity.RecoveryCapabilityProfile,
            Path.GetFullPath(destination),
            ExecuteApprovedRecoveryPlanUseCase.ComputeDestructiveDigest(
                plan.Definition.OperationGraph),
            DateTimeOffset.UtcNow, true, true);

    private static async Task<RecoverySourceInspection> SourceAsync(
        string sourceBundle,
        bool includeSummary = true)
    {
        List<(string Name, RecoverySourceArtifactKind Kind, string Content)> values =
        [
            ("approved.cleanup-plan.json", RecoverySourceArtifactKind.CleanupPlan, "plan"),
            ("execution.journal.jsonl", RecoverySourceArtifactKind.ExecutionJournal, "journal"),
            ("backup-manifest.json", RecoverySourceArtifactKind.OriginalBackupManifest, "manifest"),
        ];
        if (includeSummary)
            values.Insert(2, ("execution-summary.json",
                RecoverySourceArtifactKind.ExecutionSummary, "summary"));
        List<VerifiedRecoverySourceArtifact> artifacts = [];
        foreach ((string name, RecoverySourceArtifactKind kind, string content) in values)
        {
            string path = Path.Combine(sourceBundle, name);
            await File.WriteAllTextAsync(path, content);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            artifacts.Add(new(name, path, kind, bytes.LongLength,
                new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())));
        }
        return new(sourceBundle, null, null, null, null, null, null, null,
            artifacts, []);
    }
}
