using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed class PrepareCleanupExecutionUseCase(
    IExecutionLibraryScanner scanLibrary,
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

        LibraryScanOutcome scan = await scanLibrary.ScanFreshAsync(request.LibraryRoot, scanProgress, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
        {
            issues.Add(new("EXECUTION.SCAN_FAILED", ExecutionIssueSeverity.BlockingError,
                "A fresh read-only library scan could not be completed."));
            return new(request.Plan, tool.Tool, capability.Graph, null, null, issues, clock.GetUtcNow());
        }

        issues.AddRange(ExecutionPreflightPolicy.Evaluate(request.Plan, scan.Snapshot!));
        long requiredBytes = ExecutionPreflightPolicy.EstimateRequiredBackupBytes(request.Plan);
        BackupDestinationValidation destination = await backupStore.ValidateDestinationAsync(
            request.LibraryRoot, request.BackupDestination, requiredBytes, cancellationToken).ConfigureAwait(false);
        issues.AddRange(destination.Issues);
        return new(request.Plan, tool.Tool, capability.Graph, scan.Snapshot!.Identity.LibraryRoot,
            destination.CanonicalDestinationIdentity,
            issues, clock.GetUtcNow());
    }
}
