using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Plans;

public sealed record CleanupPlanDefinition
{
    public CleanupPlanDefinition(
        ExpectedLibraryState expectedLibraryState,
        CalibreBookId targetRecordId,
        IEnumerable<CalibreBookId> involvedRecordIds,
        MetadataRetentionInstruction metadataRetention,
        IEnumerable<FormatRetentionInstruction> formatRetentions,
        IEnumerable<FormatRemovalInstruction> formatRemovals,
        IEnumerable<RecordRemovalInstruction> recordRemovals,
        IEnumerable<BackupRequirement> backupRequirements,
        CleanupPlanProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(expectedLibraryState);
        ArgumentNullException.ThrowIfNull(involvedRecordIds);
        ArgumentNullException.ThrowIfNull(metadataRetention);
        ArgumentNullException.ThrowIfNull(formatRetentions);
        ArgumentNullException.ThrowIfNull(formatRemovals);
        ArgumentNullException.ThrowIfNull(recordRemovals);
        ArgumentNullException.ThrowIfNull(backupRequirements);
        ArgumentNullException.ThrowIfNull(provenance);
        CalibreBookId[] involved = involvedRecordIds.Distinct().OrderBy(value => value.Value).ToArray();
        FormatRetentionInstruction[] retentions = formatRetentions.OrderBy(value => value.Format, StringComparer.Ordinal).ToArray();
        FormatRemovalInstruction[] removals = formatRemovals.OrderBy(value => value.RecordId.Value)
            .ThenBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
        RecordRemovalInstruction[] records = recordRemovals.OrderBy(value => value.RecordId.Value).ToArray();
        BackupRequirement[] backups = backupRequirements.OrderBy(value => value.Kind)
            .ThenBy(value => value.RecordId?.Value ?? 0).ThenBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal).ToArray();
        if (!involved.SequenceEqual(expectedLibraryState.MemberIds)
            || !involved.Contains(targetRecordId)
            || metadataRetention.TargetRecordId != targetRecordId
            || metadataRetention.SourceRecordId != targetRecordId
            || metadataRetention.ExpectedMetadataStateRecordId != targetRecordId)
            throw new ArgumentException("The target and metadata retention must match the V1 target policy.");
        if (records.Any(value => value.RecordId == targetRecordId)
            || !records.Select(value => value.RecordId).SequenceEqual(involved.Where(value => value != targetRecordId)))
            throw new ArgumentException("Record removals must be exactly the non-target group members.", nameof(recordRemovals));
        if (retentions.Length == 0
            || retentions.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != retentions.Length
            || retentions.Any(value => value.TargetRecordId != targetRecordId))
            throw new ArgumentException("Exactly one target retention is required per final format.", nameof(formatRetentions));
        if (backups.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != backups.Length)
            throw new ArgumentException("Backup IDs must be unique.", nameof(backupRequirements));
        ExpectedLibraryState = expectedLibraryState;
        TargetRecordId = targetRecordId;
        InvolvedRecordIds = Array.AsReadOnly(involved);
        MetadataRetention = metadataRetention;
        FormatRetentions = Array.AsReadOnly(retentions);
        FormatRemovals = Array.AsReadOnly(removals);
        RecordRemovals = Array.AsReadOnly(records);
        BackupRequirements = Array.AsReadOnly(backups);
        Provenance = provenance;
    }

    public ExpectedLibraryState ExpectedLibraryState { get; }
    public CalibreBookId TargetRecordId { get; }
    public IReadOnlyList<CalibreBookId> InvolvedRecordIds { get; }
    public MetadataRetentionInstruction MetadataRetention { get; }
    public IReadOnlyList<FormatRetentionInstruction> FormatRetentions { get; }
    public IReadOnlyList<FormatRemovalInstruction> FormatRemovals { get; }
    public IReadOnlyList<RecordRemovalInstruction> RecordRemovals { get; }
    public IReadOnlyList<BackupRequirement> BackupRequirements { get; }
    public CleanupPlanProvenance Provenance { get; }
}
