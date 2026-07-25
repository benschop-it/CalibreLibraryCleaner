using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryStateBackupService
{
    Task<RecoveryBackupDestinationValidation> ValidateDestinationAsync(
        string libraryRoot,
        string destination,
        long requiredBytes,
        CancellationToken cancellationToken);

    Task<string> CreateWorkspaceAsync(
        Domain.Recoveries.RecoveryExecutionId recoveryExecutionId,
        string canonicalDestinationIdentity,
        CancellationToken cancellationToken);

    Task<RecoveryCurrentStateBackupResult> CreateAndVerifyAsync(
        CreateRecoveryCurrentStateBackupRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Domain.Recoveries.RecoveryIssue>> VerifyAvailableAsync(
        RecoveryCurrentStateBackup backup,
        CancellationToken cancellationToken);
}
