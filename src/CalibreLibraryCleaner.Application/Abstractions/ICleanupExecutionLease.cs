using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ICleanupExecutionLeaseHandle : IAsyncDisposable
{
    string LeaseIdentity { get; }
    bool IsHeld { get; }
}

public interface ICleanupExecutionLease
{
    Task<ExecutionLeaseAcquisition> TryAcquireAsync(
        ExecutionLeaseRequest request,
        CancellationToken cancellationToken);
}
