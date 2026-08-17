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
