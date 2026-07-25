using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryIdGenerator
{
    RecoveryPlanId CreatePlanId();
    RecoveryExecutionId CreateExecutionId();
}

public interface IDestructiveRecoveryConfirmation
{
    Task<bool> ConfirmAsync(
        DestructiveRecoveryConfirmationRequest request,
        CancellationToken cancellationToken);
}
