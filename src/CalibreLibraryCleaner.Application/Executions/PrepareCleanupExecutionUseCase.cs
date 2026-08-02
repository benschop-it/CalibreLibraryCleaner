using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed class PrepareCleanupExecutionUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    IExecutionBackupStore backupStore,
    IClock clock) : IPrepareCleanupExecution
{
    public async Task<CleanupExecutionPreparation> ExecuteAsync(
        PrepareCleanupExecutionRequest request,
        IProgress<LibraryScanProgress>? scanProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        List<ExecutionIssue> issues = [];
        CleanupExecutionCapabilityResult capability = CleanupExecutionCapabilityPolicy.Evaluate(request.Plan);
        issues.AddRange(capability.Issues);
        if (!capability.IsSupported)
            return new(request.Plan, null, null, null, null, issues, clock.GetUtcNow());

        CalibreToolDiscoveryResult tool = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(tool.Issues);
        if (!tool.IsSuccess)
            return new(request.Plan, tool.Tool, capability.Graph, null, null, issues, clock.GetUtcNow());

        LibraryState? state = libraryState.GetCurrent(request.LibraryRoot);
        if (state is null || !state.IsAuthoritative)
        {
            issues.Add(new("EXECUTION.STATE_UNAVAILABLE", ExecutionIssueSeverity.BlockingError,
                "Run an explicit scan to establish authoritative library state before cleanup."));
            return new(request.Plan, tool.Tool, capability.Graph, null, null, issues, clock.GetUtcNow());
        }

        issues.AddRange(ExecutionPreflightPolicy.Evaluate(request.Plan, state.Snapshot));
        long requiredBytes = ExecutionPreflightPolicy.EstimateRequiredBackupBytes(request.Plan);
        BackupDestinationValidation destination = await backupStore.ValidateDestinationAsync(
            request.LibraryRoot, request.BackupDestination, requiredBytes, cancellationToken).ConfigureAwait(false);
        issues.AddRange(destination.Issues);
        return new(request.Plan, tool.Tool, capability.Graph, state.Snapshot.Identity.LibraryRoot,
            destination.CanonicalDestinationIdentity,
            issues, clock.GetUtcNow());
    }
}
