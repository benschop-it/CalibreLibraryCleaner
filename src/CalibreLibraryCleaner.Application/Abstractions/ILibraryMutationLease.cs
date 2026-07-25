using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Application.Abstractions;

public enum LibraryMutationKind
{
    Cleanup,
    Recovery,
}

public interface ILibraryMutationLeaseHandle : IAsyncDisposable
{
    string LeaseIdentity { get; }
    bool IsHeld { get; }
    LibraryMutationKind MutationKind { get; }
}

public interface ILibraryMutationLease
{
    Task<LibraryMutationLeaseAcquisition> TryAcquireAsync(
        LibraryMutationLeaseRequest request,
        CancellationToken cancellationToken);
}

public sealed record LibraryMutationLeaseRequest(
    string OperationId,
    LibraryMutationKind MutationKind,
    string LibraryRoot,
    string LibraryUuid,
    DateTimeOffset RequestedAtUtc);

public sealed record LibraryMutationLeaseAcquisition(
    ILibraryMutationLeaseHandle? Lease,
    IReadOnlyList<Domain.Executions.ExecutionIssue> Issues)
{
    public bool IsAcquired => Lease is not null
        && Issues.All(value => value.Severity != Domain.Executions.ExecutionIssueSeverity.BlockingError);
}
