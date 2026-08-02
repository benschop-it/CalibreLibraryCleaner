using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

internal static class ProjectedRecoveryCurrentState
{
    public static RecoveryCurrentStateScanResult Create(
        LibraryState? state,
        IReadOnlyCollection<CalibreBookId> affectedRecordIds)
    {
        ArgumentNullException.ThrowIfNull(affectedRecordIds);
        if (state is null || !state.IsAuthoritative)
            return new(null,
            [
                new("RECOVERY.STATE_UNAVAILABLE", RecoveryIssueSeverity.Blocking,
                    "Projected library state", "Run an explicit scan to establish authoritative state before recovery."),
            ]);
        RecoveryCoverEvidence[] covers = state.Snapshot.Books
            .Where(value => affectedRecordIds.Contains(value.Id))
            .Select(value => new RecoveryCoverEvidence(value.Id,
                value.PublicationMetadata.HasCover, null, null))
            .ToArray();
        return new(new(state.Snapshot, covers,
            RecoverySnapshotFingerprintPolicy.ComputeFull(state.Snapshot, covers),
            RecoverySnapshotFingerprintPolicy.ComputeAffected(state.Snapshot, affectedRecordIds, covers),
            RecoverySnapshotFingerprintPolicy.ComputeUnrelated(state.Snapshot, affectedRecordIds, covers)), []);
    }
}
