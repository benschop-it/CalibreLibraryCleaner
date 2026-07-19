using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ICleanupPlanStore
{
    Task<CleanupPlanStoreWriteResult> WriteAsync(
        CleanupPlan plan,
        string libraryRoot,
        string destinationPath,
        CancellationToken cancellationToken);

    Task<CleanupPlanStoreReadResult> ReadAsync(
        string libraryRoot,
        string sourcePath,
        CancellationToken cancellationToken);
}

public sealed record CleanupPlanStoreError(string Code, string Message);

public sealed record CleanupPlanStoreWriteResult(bool IsSuccess, CleanupPlanStoreError? Error)
{
    public static CleanupPlanStoreWriteResult Success() => new(true, null);
    public static CleanupPlanStoreWriteResult Failure(string code, string message) => new(false, new(code, message));
}

public sealed record CleanupPlanStoreReadResult(CleanupPlan? Plan, CleanupPlanStoreError? Error)
{
    public bool IsSuccess => Plan is not null;
    public static CleanupPlanStoreReadResult Success(CleanupPlan plan) => new(plan, null);
    public static CleanupPlanStoreReadResult Failure(string code, string message) => new(null, new(code, message));
}
