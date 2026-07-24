using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Infrastructure.Plans;

internal sealed class SystemCleanupPlanIdGenerator : ICleanupPlanIdGenerator
{
    public CleanupPlanId Create() => new(Guid.NewGuid());
}
