using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryCalibreGateway
{
    Task<RecoveryCalibreCommandResult> CreateEmptyRecordAsync(
        CreateRecoveryRecordCommand request,
        CancellationToken cancellationToken);

    Task<RecoveryCalibreCommandResult> SetMetadataFieldAsync(
        SetRecoveryMetadataFieldCommand request,
        CancellationToken cancellationToken);

    Task<RecoveryCalibreCommandResult> AddOrReplaceFormatAsync(
        RestoreRecoveryFormatCommand request,
        CancellationToken cancellationToken);

    Task<RecoveryCalibreCommandResult> RemoveFormatAsync(
        RemoveRecoveryFormatCommand request,
        CancellationToken cancellationToken);

    Task<RecoveryCalibreCommandResult> RemoveRecordAsync(
        RemoveRecoveryRecordCommand request,
        CancellationToken cancellationToken);
}

public interface ICalibreExecutionProfileProvider
{
    RecoveryCapabilityProfile EvaluateRecoveryProfile(Executions.CalibreToolDescriptor tool);
}
