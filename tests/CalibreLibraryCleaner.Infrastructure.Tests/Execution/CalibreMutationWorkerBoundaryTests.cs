using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

[Collection(ProcessEnvironmentGroup.Name)]
public sealed class CalibreMutationWorkerBoundaryTests
{
    [Fact]
    public async Task ManagedNameSkipHasNoInventedReadBackAndTheFollowingOperationCompletes()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        CalibreMutationWorkerOpenResult opened = await provider.GetRequiredService<ICalibreMutationWorkerFactory>()
            .TryOpenAsync(new(tool, library, "87f7ed1f-59a8-45a6-975a-7e06fd84780d"), CancellationToken.None);
        await using ICalibreMutationWorkerSession session = opened.Session!;

        CalibreMutationChunkResult result = await session.ExecuteChunkAsync(new("naming-skip", [
            CalibreMutationOperation.RefreshManagedName("skip-unchanged:name:1", new(1)),
            CalibreMutationOperation.RefreshManagedName("refresh:2", new(2)),
        ]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        result.OperationResults[0].IsSkipped.Should().BeTrue();
        result.OperationResults[0].VerifiedManagedPath.Should().BeNull();
        result.OperationResults[0].VerifiedFormats.Should().BeNull();
        result.OperationResults[0].SkipCode.Should().Be("managed_name_update_path_os_error_errno_13_winerror_32");
        result.OperationResults[1].IsSkipped.Should().BeFalse();
        result.OperationResults[1].VerifiedManagedPath.Should().Be("Verified Author/Verified Title (2)");
    }

    [Fact]
    public async Task RemovalCannotClaimAVerifiedUnchangedSkip()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        CalibreMutationWorkerOpenResult opened = await provider.GetRequiredService<ICalibreMutationWorkerFactory>()
            .TryOpenAsync(new(tool, library, "87f7ed1f-59a8-45a6-975a-7e06fd84780d"), CancellationToken.None);
        await using ICalibreMutationWorkerSession session = opened.Session!;

        CalibreMutationChunkResult result = await session.ExecuteChunkAsync(new("invalid-skip",
            [CalibreMutationOperation.RemoveRecord("skip-unchanged:remove:1", new(1))]), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("CALIBRE_WORKER_PROTOCOL_INVALID");
    }

    [Fact]
    public async Task OnePersistentProcessExecutesMultipleChunks()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string log = Path.Combine(temporary.Path, "worker-starts.jsonl");
        executable.SetLogPath(log);
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        ICalibreMutationWorkerFactory factory = provider.GetRequiredService<ICalibreMutationWorkerFactory>();

        CalibreMutationWorkerOpenResult opened = await factory.TryOpenAsync(new(
            tool, library, "87f7ed1f-59a8-45a6-975a-7e06fd84780d"), CancellationToken.None);
        await using ICalibreMutationWorkerSession session = opened.Session!;
        CalibreMutationChunkResult first = await session.ExecuteChunkAsync(new("chunk-1",
            [CalibreMutationOperation.RemoveFormat("remove:2:EPUB", new(2), "EPUB")]),
            CancellationToken.None);
        CalibreMutationChunkResult second = await session.ExecuteChunkAsync(new("chunk-2",
            [CalibreMutationOperation.RemoveRecord("remove-record:2", new(2))]),
            CancellationToken.None);

        opened.IsSuccess.Should().BeTrue(opened.FailureCode);
        first.IsSuccess.Should().BeTrue(first.FailureCode);
        second.IsSuccess.Should().BeTrue(second.FailureCode);
        File.ReadLines(log).Should().ContainSingle();
    }

    [Fact]
    public async Task QuarantineCopyReturnsDestinationIdentityAndVerifiedRecord()
    {
        using ControlledCalibreExecutable executable = new();
        string root = Path.Combine(Path.GetTempPath(), "clcq" + Guid.NewGuid().ToString("N")[..6]);
        string library = Path.Combine(root, "S");
        string destination = Path.Combine(root, "S - Unresolvable");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        try
        {
            using ServiceProvider provider = Provider(executable);
            CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
                .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
            CalibreMutationWorkerOpenResult opened = await provider
                .GetRequiredService<ICalibreMutationWorkerFactory>()
                .TryOpenAsync(new(
                    tool,
                    library,
                    "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
                    QuarantineDestinationRoot: destination), CancellationToken.None);
            await using ICalibreMutationWorkerSession session = opened.Session!;
            FormatFileFingerprint fingerprint = new(10, new(new string('a', 64)));
            CalibreQuarantineRecordExpectation expectation = new(
                "Reviewed Title",
                ["Reviewed Author"],
                ["Author, Reviewed"],
                [new("EPUB", "Reviewed Author/Reviewed Title (1)/book.epub", fingerprint)]);

            CalibreMutationChunkResult result = await session.ExecuteChunkAsync(new(
                "quarantine-copy",
                [CalibreMutationOperation.CopyRecordToQuarantine("quarantine:copy:1", new(1), expectation)]),
                CancellationToken.None);

            opened.IsSuccess.Should().BeTrue(opened.FailureCode);
            opened.QuarantineLibraryUuid.Should().Be("test-quarantine-uuid");
            opened.QuarantineLibraryExisted.Should().BeFalse();
            CalibreMutationOperationResult operation = result.OperationResults.Should().ContainSingle().Subject;
            operation.QuarantineRecordId.Should().Be(new CalibreBookId(1001));
            operation.VerifiedFormats.Should().ContainSingle().Which.Fingerprint.Should().Be(fingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataOperationReturnsVerifiedValuesPathAndAuthorSort()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        ICalibreMutationWorkerFactory factory = provider.GetRequiredService<ICalibreMutationWorkerFactory>();
        CalibreMetadataSourceIdentity source = new(
            "qualified-provider", "1.0", "qualified-edition", "qualified-policy/1.0");

        CalibreMutationWorkerOpenResult opened = await factory.TryOpenAsync(new(
            tool, library, "87f7ed1f-59a8-45a6-975a-7e06fd84780d"), CancellationToken.None);
        await using ICalibreMutationWorkerSession session = opened.Session!;
        CalibreMutationChunkResult result = await session.ExecuteChunkAsync(new(
            "metadata-chunk",
            [CalibreMutationOperation.SetMetadata(
                "metadata:1:title",
                new(1),
                new(LibraryMetadataField.Title, ["Qualified Title"], source))]),
            CancellationToken.None);

        opened.IsSuccess.Should().BeTrue(opened.FailureCode);
        result.IsSuccess.Should().BeTrue(result.FailureCode);
        CalibreMutationOperationResult operation = result.OperationResults.Should().ContainSingle().Subject;
        operation.VerifiedMetadataValues.Should().Equal("Qualified Title");
        operation.VerifiedManagedPath.Should().Be("Verified/Managed/Path");
        operation.VerifiedAuthorSort.Should().Be("Verified Author Sort");
    }

    [Fact]
    public async Task AuthorNormalizationReturnsVerifiedPerAuthorSorts()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        CalibreMetadataSourceIdentity source = new(
            "calibre-author-normalization", "1.0", "local-library", "author-normalization/1.0");

        CalibreMutationWorkerOpenResult opened = await provider
            .GetRequiredService<ICalibreMutationWorkerFactory>()
            .TryOpenAsync(new(tool, library, "87f7ed1f-59a8-45a6-975a-7e06fd84780d"),
                CancellationToken.None);
        await using ICalibreMutationWorkerSession session = opened.Session!;
        CalibreMutationChunkResult result = await session.ExecuteChunkAsync(new(
            "author-normalization",
            [CalibreMutationOperation.SetMetadata(
                "authors:1",
                new(1),
                new(
                    LibraryMetadataField.Authors,
                    ["Frederik Pohl", "Jack Williamson"],
                    source,
                    authorSortValues: ["Pohl, Frederik", "Williamson, Jack"]))]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        CalibreMutationOperationResult operation = result.OperationResults.Should().ContainSingle().Subject;
        operation.VerifiedMetadataValues.Should().Equal("Frederik Pohl", "Jack Williamson");
        operation.VerifiedAuthorSortValues.Should().Equal("Pohl, Frederik", "Williamson, Jack");
    }

    [Fact]
    public async Task ManagedNameRefreshReturnsVerifiedPathBasenamesAndFingerprints()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        CalibreMutationWorkerOpenResult opened = await provider
            .GetRequiredService<ICalibreMutationWorkerFactory>()
            .TryOpenAsync(new(tool, library, "87f7ed1f-59a8-45a6-975a-7e06fd84780d"),
                CancellationToken.None);
        await using ICalibreMutationWorkerSession session = opened.Session!;

        CalibreMutationChunkResult result = await session.ExecuteChunkAsync(new(
            "managed-name",
            [CalibreMutationOperation.RefreshManagedName("refresh:1", new(1))]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        CalibreMutationOperationResult operation = result.OperationResults.Should().ContainSingle().Subject;
        operation.VerifiedManagedPath.Should().Be("Verified Author/Verified Title (1)");
        ManagedFormatReadBack format = operation.VerifiedFormats.Should().ContainSingle().Subject;
        format.StoredFileName.Should().Be("Verified Title - Verified Author.epub");
        format.Fingerprint.Should().Be(new FormatFileFingerprint(10, new(new string('a', 64))));
    }

    [Fact]
    public async Task VerifiedUnchangedMetadataReturnsTypedSkipWithoutVerifiedMutationValues()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        CalibreMetadataSourceIdentity source = new(
            "qualified-provider", "1.0", "qualified-edition", "qualified-policy/1.0");

        CalibreMutationWorkerOpenResult opened = await provider
            .GetRequiredService<ICalibreMutationWorkerFactory>()
            .TryOpenAsync(new(tool, library, "87f7ed1f-59a8-45a6-975a-7e06fd84780d"),
                CancellationToken.None);
        await using ICalibreMutationWorkerSession session = opened.Session!;
        CalibreMutationChunkResult result = await session.ExecuteChunkAsync(new(
            "metadata-skip-chunk",
            [CalibreMutationOperation.SetMetadata(
                "skip-unchanged:metadata:1:publisher",
                new(1),
                new(LibraryMetadataField.Publisher, ["Proposed"], source))]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        CalibreMutationOperationResult operation = result.OperationResults.Should().ContainSingle().Subject;
        operation.IsSkipped.Should().BeTrue();
        operation.SkipCode.Should().Be("metadata_values_invalid");
        operation.VerifiedMetadataValues.Should().BeNull();
        operation.VerifiedManagedPath.Should().BeNull();
        operation.VerifiedAuthorSort.Should().BeNull();
    }

    [Fact]
    public async Task MissingTrustedSiblingFailsBeforeMutation()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        using ServiceProvider provider = Provider(executable);
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        CalibreToolDescriptor tool = (await provider.GetRequiredService<ICalibreToolDiscovery>()
            .DiscoverAndProbeAsync(library, CancellationToken.None)).Tool!;
        File.Delete(executable.DebugExecutablePath);

        CalibreMutationWorkerOpenResult result = await provider
            .GetRequiredService<ICalibreMutationWorkerFactory>()
            .TryOpenAsync(new(tool, library, "library-uuid"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("CALIBRE_WORKER_NOT_FOUND");
    }

    [Fact]
    public async Task ProtocolRejectsDuplicateProperties()
    {
        const string json = "{\"protocolVersion\":\"one\",\"protocolVersion\":\"two\",\"kind\":\"ready\",\"libraryUuid\":\"id\",\"capabilities\":[]}";
        using StringReader reader = new(json + Environment.NewLine);

        Func<Task> read = async () => await CalibreMutationWorkerProtocol.ReadAsync<
            CalibreMutationWorkerReadyMessage>(reader, 4096, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidDataException>();
    }

    private static ServiceProvider Provider(ControlledCalibreExecutable executable)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new CalibreExecutionOptions
        {
            TrustedExecutablePath = executable.ExecutablePath,
            ControlledConfigDirectory = executable.ConfigDirectory,
            IsValidatedCompatibilityProfileEnabled = true,
            ProbeTimeout = TimeSpan.FromSeconds(5),
            WorkerStartupTimeout = TimeSpan.FromSeconds(5),
            WorkerShutdownTimeout = TimeSpan.FromSeconds(5),
        });
        return services.BuildServiceProvider();
    }
}
