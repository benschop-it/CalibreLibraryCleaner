using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ICalibreToolDiscovery
{
    Task<CalibreToolDiscoveryResult> DiscoverAndProbeAsync(
        string libraryRoot,
        CancellationToken cancellationToken);
}

public interface ICleanupExecutionIdGenerator
{
    CleanupExecutionId Create();
}

public interface ICalibreMutationWorkerFactory
{
    Task<CalibreMutationWorkerOpenResult> TryOpenAsync(
        OpenCalibreMutationWorkerRequest request,
        CancellationToken cancellationToken);
}

public interface ICalibreMutationWorkerSession : IAsyncDisposable
{
    Task<CalibreMutationChunkResult> ExecuteChunkAsync(
        CalibreMutationChunkRequest request,
        CancellationToken cancellationToken);
}
