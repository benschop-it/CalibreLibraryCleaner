using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public static class RecoverySnapshotFingerprintPolicy
{
    public static Sha256Digest ComputeFull(LibrarySnapshot snapshot, IEnumerable<RecoveryCoverEvidence>? covers = null) =>
        Compute(snapshot, snapshot.Books.Select(value => value.Id), covers);

    public static Sha256Digest ComputeAffected(
        LibrarySnapshot snapshot,
        IEnumerable<CalibreBookId> affectedRecordIds,
        IEnumerable<RecoveryCoverEvidence>? covers = null) =>
        Compute(snapshot, affectedRecordIds, covers);

    public static Sha256Digest ComputeUnrelated(
        LibrarySnapshot snapshot,
        IEnumerable<CalibreBookId> affectedRecordIds,
        IEnumerable<RecoveryCoverEvidence>? covers = null)
    {
        HashSet<CalibreBookId> affected = affectedRecordIds.ToHashSet();
        return Compute(snapshot, snapshot.Books.Where(value => !affected.Contains(value.Id)).Select(value => value.Id), covers);
    }

    public static Sha256Digest ComputeCanonicalRootIdentity(string canonicalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        string identity = canonicalRoot.Trim().Replace('\\', '/').ToUpperInvariant();
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant());
    }

    private static Sha256Digest Compute(
        LibrarySnapshot snapshot,
        IEnumerable<CalibreBookId> recordIds,
        IEnumerable<RecoveryCoverEvidence>? covers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<CalibreBookId> selected = recordIds.ToHashSet();
        Dictionary<CalibreBookId, RecoveryCoverEvidence> coverByRecord = (covers ?? [])
            .ToDictionary(value => value.RecordId);
        StringBuilder canonical = new();
        Add(canonical, "cleanup-recovery-snapshot-fingerprint/1.0");
        Add(canonical, snapshot.Identity.CalibreLibraryUuid);
        Add(canonical, snapshot.Identity.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        foreach (CalibreBook book in snapshot.Books.Where(value => selected.Contains(value.Id)).OrderBy(value => value.Id.Value))
        {
            Add(canonical, book.Id.Value.ToString(CultureInfo.InvariantCulture));
            Add(canonical, RecoveryRecordFingerprintPolicy.Compute(book));
            Add(canonical, book.RelativeDirectory.Replace('\\', '/'));
            foreach (BookFormat format in book.Formats.OrderBy(value => value.Format, StringComparer.Ordinal)
                         .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal))
            {
                Add(canonical, format.Format);
                Add(canonical, format.StoredFileName);
                Add(canonical, format.ExpectedRelativePath.Replace('\\', '/'));
                Add(canonical, format.FileStatus.ToString());
                Add(canonical, format.Fingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.Fingerprint?.Sha256.Value ?? string.Empty);
                Add(canonical, format.Observation?.Length.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.Observation?.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.Observation?.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.Observation?.Attributes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            }
            if (coverByRecord.TryGetValue(book.Id, out RecoveryCoverEvidence? cover))
            {
                Add(canonical, cover.HasCover ? "1" : "0");
                Add(canonical, cover.Fingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, cover.Fingerprint?.Sha256.Value ?? string.Empty);
            }
            else
            {
                Add(canonical, book.PublicationMetadata.HasCover ? "present-unhashed" : "absent");
            }
        }
        foreach (LibraryFinding finding in snapshot.Findings
                     .Where(value => value.BookId is not null && selected.Contains(value.BookId.Value))
                     .OrderBy(value => value.BookId?.Value ?? 0)
                     .ThenBy(value => value.Code, StringComparer.Ordinal)
                     .ThenBy(value => value.Format, StringComparer.Ordinal)
                     .ThenBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            Add(canonical, finding.BookId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, finding.Code);
            Add(canonical, finding.Format ?? string.Empty);
            Add(canonical, finding.RelativePath ?? string.Empty);
        }
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
    }

    private static void Add(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}
