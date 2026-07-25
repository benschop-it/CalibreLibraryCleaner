using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryHistoryStore
{
    Task<bool> HasUnresolvedRecoveryAsync(
        string libraryUuid,
        string libraryRoot,
        string? excludingSourceExecutionId,
        CancellationToken cancellationToken);

    Task RecordAsync(
        RecoveryHistoryEntry entry,
        string libraryRoot,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecoveryHistoryEntry>> ReadAsync(
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken);
}

public interface IRecoveryResolutionStore
{
    Task<bool> IsSourceExecutionResolvedAsync(
        string sourceExecutionId,
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken);

    Task RecordResolutionAsync(
        RecoveryResolutionEntry entry,
        string libraryRoot,
        CancellationToken cancellationToken);
}
