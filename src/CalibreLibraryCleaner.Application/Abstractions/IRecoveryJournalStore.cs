using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryJournalSession : IAsyncDisposable
{
    string JournalIdentity { get; }
    string? FinalEntryHash { get; }
    bool IsAvailable { get; }
    Task AppendAsync(RecoveryJournalEvent value, CancellationToken cancellationToken);
    Task CompleteAsync(RecoveryHistoryEntry value, CancellationToken cancellationToken);
}

public interface IRecoveryJournalStore
{
    Task<IRecoveryJournalSession> CreateAsync(
        RecoveryJournalCreateRequest request,
        CancellationToken cancellationToken);

    Task<RecoveryJournalReconciliationResult> ReconcileAsync(
        string canonicalDestinationIdentity,
        string libraryUuid,
        CancellationToken cancellationToken);
}
