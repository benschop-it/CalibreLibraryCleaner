using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Application.Abstractions;

public sealed class ExecutionJournalPersistenceException : Exception
{
    public ExecutionJournalPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IExecutionJournalSession : IAsyncDisposable
{
    string JournalIdentity { get; }
    bool IsAvailable { get; }

    Task AppendAsync(ExecutionJournalEvent entry, CancellationToken cancellationToken);
    Task CompleteAsync(ExecutionHistoryEntry terminalEntry, CancellationToken cancellationToken);
}

public interface IExecutionJournalStore
{
    Task<IExecutionJournalSession> CreateAsync(
        ExecutionJournalCreateRequest request,
        CancellationToken cancellationToken);

    Task<JournalReconciliationResult> ReconcileAsync(
        string canonicalBackupDestinationIdentity,
        string libraryUuid,
        CancellationToken cancellationToken);
}

public interface IExecutionHistoryStore
{
    Task<bool> HasRecoveryRequiredAsync(
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken);

    Task RecordAsync(
        ExecutionHistoryEntry entry,
        string libraryRoot,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionHistoryEntry>> ReadAsync(
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken);
}
