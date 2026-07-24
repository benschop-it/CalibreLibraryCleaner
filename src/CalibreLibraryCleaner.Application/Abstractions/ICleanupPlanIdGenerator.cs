using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ICleanupPlanIdGenerator
{
    CleanupPlanId Create();
}
