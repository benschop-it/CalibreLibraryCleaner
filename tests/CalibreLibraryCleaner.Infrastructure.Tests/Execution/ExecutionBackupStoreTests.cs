using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

public sealed class ExecutionBackupStoreTests
{
    [Fact]
    public async Task CompleteExternalBackupRehashesEveryRawAndExportedFormatAndSealsManifest()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        string backupParent = Path.Combine(temporary.Path, "backups");
        Directory.CreateDirectory(backupParent);
        using ServiceProvider provider = Provider(temporary.Path);
        IExecutionBackupStore store = provider.GetRequiredService<IExecutionBackupStore>();
        CleanupExecutionId executionId = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        BackupDestinationValidation destination = await store.ValidateDestinationAsync(
            fixture.LibraryRoot, backupParent, 1, CancellationToken.None);
        ExecutionWorkspace workspace = await store.CreateWorkspaceAsync(executionId,
            destination.CanonicalDestinationIdentity!, CancellationToken.None);
        await using IExecutionJournalSession journal = await provider.GetRequiredService<IExecutionJournalStore>().CreateAsync(new(
            workspace, fixture.Plan, fixture.LibraryRoot, "1.0.0", DateTimeOffset.UtcNow), CancellationToken.None);
        CalibreToolDescriptor tool = Tool();

        ExecutionBackupInputs inputs = await store.CreateInputsAsync(new(workspace, fixture.Plan,
            Confirmation(fixture, workspace, tool), fixture.LibraryRoot, tool, "1.0.0",
            new(new string('c', 64)), DateTimeOffset.UtcNow), CancellationToken.None);
        inputs.Issues.Should().BeEmpty();
        InfrastructureExecutionTestData.CreateExports(inputs, fixture);
        ExecutionBackupResult result = await store.VerifyAndSealAsync(new(inputs, fixture.Plan,
            fixture.LibraryRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Manifest!.Entries.Should().Contain(value => value.Kind == BackupArtifactKind.CleanupPlan);
        result.Manifest.Entries.Should().ContainSingle(value =>
            value.Kind == BackupArtifactKind.LocalExecutionConfirmation);
        result.Manifest.Entries.Count(value => value.Kind == BackupArtifactKind.RawFormat).Should().Be(1);
        result.Manifest.Entries.Count(value => value.Kind == BackupArtifactKind.ExportedFormat).Should().Be(1);
        result.Manifest.Entries.Count(value => value.Kind == BackupArtifactKind.RecordMetadataOpf).Should().Be(2);
        BackupManifestCoveragePolicy.Validate(fixture.Plan, result.Manifest).Should().BeEmpty();
        (await store.VerifyAvailableAsync(workspace, result.Manifest, CancellationToken.None)).Should().BeEmpty();
        File.ReadAllBytes(result.RawFormatBackupPaths.Single().Value).Should().Equal(fixture.FormatBytes);
    }

    [Fact]
    public async Task UnmodeledExtraDataBlocksBeforeManifestIsSealed()
    {
        using BackupHarness harness = await BackupHarness.CreateAsync();
        InfrastructureExecutionTestData.CreateExports(harness.Inputs, harness.Fixture, includeExtra: true);

        ExecutionBackupResult result = await harness.Store.VerifyAndSealAsync(new(harness.Inputs, harness.Fixture.Plan,
            harness.Fixture.LibraryRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.UNMODELED_EXTRA_DATA");
    }

    [Fact]
    public async Task ExportedDirectoryBlocksWithoutRecursiveTraversal()
    {
        using BackupHarness harness = await BackupHarness.CreateAsync();
        InfrastructureExecutionTestData.CreateExports(harness.Inputs, harness.Fixture);
        string extraDirectory = Path.Combine(
            harness.Inputs.ExportDirectories[new CalibreBookId(2)], "extra-data");
        Directory.CreateDirectory(extraDirectory);
        File.WriteAllText(Path.Combine(extraDirectory, "payload.bin"), "unmodeled");

        ExecutionBackupResult result = await harness.Store.VerifyAndSealAsync(new(
            harness.Inputs, harness.Fixture.Plan, harness.Fixture.LibraryRoot,
            DateTimeOffset.UtcNow), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.UNMODELED_EXTRA_DATA");
    }

    [Fact]
    public async Task ChangedRawBackupFailsIndependentHashVerification()
    {
        using BackupHarness harness = await BackupHarness.CreateAsync();
        InfrastructureExecutionTestData.CreateExports(harness.Inputs, harness.Fixture);
        File.WriteAllText(harness.Inputs.RawFormatBackupPaths.Single().Value, "changed");

        ExecutionBackupResult result = await harness.Store.VerifyAndSealAsync(new(harness.Inputs, harness.Fixture.Plan,
            harness.Fixture.LibraryRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.BACKUP_RAW_FORMAT_INVALID");
    }

    [Fact]
    public async Task LibraryOrAncestorDestinationIsRejectedWithoutCreatingBackupFiles()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
        using ServiceProvider provider = Provider(Path.Combine(temporary.Path, "storage"));
        IExecutionBackupStore store = provider.GetRequiredService<IExecutionBackupStore>();

        BackupDestinationValidation inside = await store.ValidateDestinationAsync(
            fixture.LibraryRoot, fixture.LibraryRoot, 1, CancellationToken.None);
        BackupDestinationValidation ancestor = await store.ValidateDestinationAsync(
            fixture.LibraryRoot, temporary.Path, 1, CancellationToken.None);

        inside.IsValid.Should().BeFalse();
        ancestor.IsValid.Should().BeFalse();
        inside.Issues.Should().Contain(value => value.Code == "EXECUTION.BACKUP_DESTINATION_UNSAFE");
    }

    [Fact]
    public async Task ReportedCoverMustBePresentInExport()
    {
        using TemporaryDirectory temporary = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path, targetHasCover: true);
        string backupParent = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(backupParent);
        using ServiceProvider provider = Provider(Path.Combine(temporary.Path, "storage"));
        IExecutionBackupStore store = provider.GetRequiredService<IExecutionBackupStore>();
        ExecutionWorkspace workspace = await store.CreateWorkspaceAsync(new(Guid.NewGuid()), backupParent, CancellationToken.None);
        File.WriteAllText(Path.Combine(workspace.BundlePath, "execution.journal.jsonl"), "journal");
        CalibreToolDescriptor tool = Tool();
        ExecutionBackupInputs inputs = await store.CreateInputsAsync(new(workspace, fixture.Plan,
            Confirmation(fixture, workspace, tool), fixture.LibraryRoot,
            tool, "1.0.0", new(new string('c', 64)), DateTimeOffset.UtcNow), CancellationToken.None);
        inputs.Issues.Should().BeEmpty();
        InfrastructureExecutionTestData.CreateExports(inputs, fixture);
        File.Delete(Path.Combine(inputs.ExportDirectories[new CalibreBookId(1)], "cover.jpg"));

        ExecutionBackupResult result = await store.VerifyAndSealAsync(new(inputs, fixture.Plan,
            fixture.LibraryRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.BACKUP_COVER_MISSING");
    }

    [Fact]
    public async Task EveryAffectedRecordRequiresExportedMetadataOpf()
    {
        using BackupHarness harness = await BackupHarness.CreateAsync();
        InfrastructureExecutionTestData.CreateExports(harness.Inputs, harness.Fixture);
        File.Delete(Path.Combine(harness.Inputs.ExportDirectories[new CalibreBookId(2)], "metadata.opf"));

        ExecutionBackupResult result = await harness.Store.VerifyAndSealAsync(new(harness.Inputs,
            harness.Fixture.Plan, harness.Fixture.LibraryRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.BACKUP_METADATA_MISSING");
    }

    private sealed class BackupHarness : IDisposable
    {
        private BackupHarness(TemporaryDirectory temporary, ServiceProvider provider,
            InfrastructureExecutionFixture fixture, IExecutionBackupStore store, ExecutionBackupInputs inputs)
        {
            Temporary = temporary;
            Provider = provider;
            Fixture = fixture;
            Store = store;
            Inputs = inputs;
        }

        public TemporaryDirectory Temporary { get; }
        public ServiceProvider Provider { get; }
        public InfrastructureExecutionFixture Fixture { get; }
        public IExecutionBackupStore Store { get; }
        public ExecutionBackupInputs Inputs { get; }

        public static async Task<BackupHarness> CreateAsync()
        {
            TemporaryDirectory temporary = new();
            InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(temporary.Path);
            string backupParent = Path.Combine(temporary.Path, "external");
            Directory.CreateDirectory(backupParent);
            ServiceProvider provider = ProviderFor(Path.Combine(temporary.Path, "storage"));
            IExecutionBackupStore store = provider.GetRequiredService<IExecutionBackupStore>();
            ExecutionWorkspace workspace = await store.CreateWorkspaceAsync(new(Guid.NewGuid()), backupParent, CancellationToken.None);
            File.WriteAllText(Path.Combine(workspace.BundlePath, "execution.journal.jsonl"), "journal");
            CalibreToolDescriptor tool = Tool();
            ExecutionBackupInputs inputs = await store.CreateInputsAsync(new(workspace, fixture.Plan,
                Confirmation(fixture, workspace, tool), fixture.LibraryRoot,
                tool, "1.0.0", new(new string('c', 64)), DateTimeOffset.UtcNow), CancellationToken.None);
            inputs.Issues.Should().BeEmpty();
            return new(temporary, provider, fixture, store, inputs);
        }

        public void Dispose()
        {
            Provider.Dispose();
            Temporary.Dispose();
        }
    }

    private static ServiceProvider Provider(string storageRoot)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new ExecutionStorageOptions
        {
            LeaseRoot = Path.Combine(storageRoot, "leases"),
            HistoryRoot = Path.Combine(storageRoot, "history"),
        });
        return services.BuildServiceProvider();
    }

    private static ServiceProvider ProviderFor(string storageRoot) => Provider(storageRoot);

    private static CalibreToolDescriptor Tool() => new("C:\\trusted\\calibredb.exe",
        new("C:\\trusted\\calibredb.exe", "9.11.0", new(new string('b', 64)), "calibredb/windows/9.11.0"),
        Enum.GetValues<CalibreExecutionCapability>());

    private static CleanupExecutionConfirmation Confirmation(
        InfrastructureExecutionFixture fixture,
        ExecutionWorkspace workspace,
        CalibreToolDescriptor tool) => new(
        fixture.Plan.Id,
        fixture.Plan.ArtifactRevision,
        fixture.Plan.ContentDigest,
        fixture.Plan.InputIdentity.LibraryUuid,
        fixture.LibraryRoot,
        CleanupExecutionCapabilityPolicy.Evaluate(fixture.Plan).Graph?.Digest
            ?? new Sha256Digest(new string('d', 64)),
        tool.Identity,
        workspace.CanonicalBackupDestinationIdentity,
        DateTimeOffset.UtcNow,
        true,
        true);
}
