using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IExecutionBackupStore
{
    Task<BackupDestinationValidation> ValidateDestinationAsync(
        string libraryRoot,
        string backupDestination,
        long requiredBytes,
        CancellationToken cancellationToken);

    Task<ExecutionWorkspace> CreateWorkspaceAsync(
        CleanupExecutionId executionId,
        string canonicalBackupDestinationIdentity,
        CancellationToken cancellationToken);

    Task<ExecutionBackupInputs> CreateInputsAsync(
        CreateBackupInputsRequest request,
        CancellationToken cancellationToken);

    Task<ExecutionBackupResult> VerifyAndSealAsync(
        SealBackupRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionIssue>> VerifyAvailableAsync(
        ExecutionWorkspace workspace,
        VerifiedBackupManifest manifest,
        CancellationToken cancellationToken);
}
