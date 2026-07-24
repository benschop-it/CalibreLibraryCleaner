using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal sealed class SystemCleanupExecutionIdGenerator : ICleanupExecutionIdGenerator
{
    public CleanupExecutionId Create() => new(Guid.NewGuid());
}
