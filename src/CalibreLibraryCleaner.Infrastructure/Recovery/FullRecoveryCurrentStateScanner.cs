using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal sealed class FullRecoveryCurrentStateScanner(
    IExecutionLibraryScanner scanner) : IRecoveryCurrentStateScanner
{
    public async Task<RecoveryCurrentStateScanResult> ScanFreshAsync(
        string libraryRoot,
        IReadOnlyCollection<CalibreBookId> affectedRecordIds,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(affectedRecordIds);
        LibraryScanOutcome outcome = await scanner.ScanFreshAsync(
            libraryRoot, progress, cancellationToken).ConfigureAwait(false);
        if (outcome.Snapshot is null)
        {
            string explanation = outcome.Error?.Message
                ?? "The complete read-only library scan did not return a snapshot.";
            return new(null,
            [
                new("RECOVERY.CURRENT_SCAN_FAILED", RecoveryIssueSeverity.Blocking,
                    "Current library", explanation),
            ]);
        }

        List<RecoveryIssue> issues = [];
        List<RecoveryCoverEvidence> covers = [];
        foreach (CalibreBook book in outcome.Snapshot.Books
                     .Where(value => affectedRecordIds.Contains(value.Id)))
        {
            if (!book.PublicationMetadata.HasCover)
            {
                covers.Add(new(book.Id, false, null, null));
                continue;
            }
            string relativePath = Path.Combine(book.RelativeDirectory, "cover.jpg");
            if (!ExecutionPathGuard.TryValidateContainedRegularFile(
                    libraryRoot, relativePath, out string? coverPath, out _))
            {
                issues.Add(new("RECOVERY.CURRENT_COVER_UNREADABLE",
                    RecoveryIssueSeverity.Blocking, $"Current record {book.Id}",
                    "The current cover flag is set but exact cover bytes cannot be read safely.",
                    currentRecordId: book.Id));
                covers.Add(new(book.Id, true, null, null));
                continue;
            }
            try
            {
                FileInfo info = new(coverPath!);
                await using FileStream stream = new(coverPath!, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                Sha256Digest digest = new(Convert.ToHexString(
                    await System.Security.Cryptography.SHA256.HashDataAsync(
                        stream, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant());
                covers.Add(new(book.Id, true,
                    new(info.Length, digest), coverPath));
            }
            catch (Exception exception) when (exception is IOException
                       or UnauthorizedAccessException)
            {
                issues.Add(new("RECOVERY.CURRENT_COVER_UNREADABLE",
                    RecoveryIssueSeverity.Blocking, $"Current record {book.Id}",
                    "The current cover changed or became unreadable during the fresh scan.",
                    currentRecordId: book.Id));
                covers.Add(new(book.Id, true, null, null));
            }
        }
        RecoveryCurrentStateSnapshot current = new(
            outcome.Snapshot,
            covers,
            RecoverySnapshotFingerprintPolicy.ComputeFull(outcome.Snapshot, covers),
            RecoverySnapshotFingerprintPolicy.ComputeAffected(
                outcome.Snapshot, affectedRecordIds, covers),
            RecoverySnapshotFingerprintPolicy.ComputeUnrelated(
                outcome.Snapshot, affectedRecordIds, covers));
        return new(current, issues);
    }
}
