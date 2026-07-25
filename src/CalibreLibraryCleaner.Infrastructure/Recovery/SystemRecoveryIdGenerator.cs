using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal sealed class SystemRecoveryIdGenerator : IRecoveryIdGenerator
{
    public RecoveryPlanId CreatePlanId() => new(Guid.NewGuid());

    public RecoveryExecutionId CreateExecutionId() => new(Guid.NewGuid());
}
