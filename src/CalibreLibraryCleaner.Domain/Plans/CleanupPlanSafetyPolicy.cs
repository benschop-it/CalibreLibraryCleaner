using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Domain.Plans;

public static class CleanupPlanSafetyPolicy
{
    public static IReadOnlyList<CleanupPlanIssue> Validate(CleanupPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<CleanupPlanIssue> issues = [];
        Dictionary<(CalibreBookId RecordId, string Format, string Path), ExpectedFormatState> expected = definition.ExpectedLibraryState.Records
            .SelectMany(record => record.Formats)
            .ToDictionary(format => (format.RecordId, format.Format, format.RelativePath));
        Dictionary<string, FormatRetentionInstruction> retainedByFormat = definition.FormatRetentions
            .ToDictionary(value => value.Format, StringComparer.Ordinal);
        if (definition.FormatRetentions.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count()
            != definition.FormatRetentions.Count)
            AddCoverage(issues, null, null, "Retained-format instruction IDs must be unique.");
        HashSet<(CalibreBookId, string, string)> retainedAssociations = [];
        HashSet<(CalibreBookId, string, string)> removedAssociations = [];
        HashSet<string> backupIds = definition.BackupRequirements.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);

        foreach (FormatRetentionInstruction retention in definition.FormatRetentions)
        {
            var key = (retention.SourceState.RecordId, retention.Format, retention.SourceState.RelativePath);
            if (!retainedAssociations.Add(key)
                || !expected.TryGetValue(key, out ExpectedFormatState? canonical)
                || canonical != retention.SourceState)
                AddCoverage(issues, retention.SourceState.RecordId, retention.Format, "A retained source does not exactly match the canonical expected format state.");
        }

        foreach (FormatRemovalInstruction removal in definition.FormatRemovals)
        {
            var key = (removal.RecordId, removal.Format, removal.RelativePath);
            retainedByFormat.TryGetValue(removal.Format, out FormatRetentionInstruction? retained);
            bool sourceRemovedAfterRetention = removal.Reason == FormatRemovalReason.RemovedWithSourceRecordAfterRetention;
            if (!removedAssociations.Add(key)
                || !expected.TryGetValue(key, out ExpectedFormatState? canonical)
                || canonical != removal.ExpectedState
                || retained is null
                || retained.Id != removal.RetainedFormatInstructionId
                || !backupIds.Contains(removal.BackupRequirementId)
                || !definition.BackupRequirements.Any(value => value.Id == removal.BackupRequirementId
                    && value.Kind == BackupRequirementKind.FormatFile
                    && value.RecordId == removal.RecordId
                    && value.Format == removal.Format)
                || sourceRemovedAfterRetention != (retained?.SourceState == removal.ExpectedState
                    && removal.RecordId != definition.TargetRecordId))
                AddCoverage(issues, removal.RecordId, removal.Format, "A format removal lacks a unique retained destination or backup.");
            if (removal.BytesIdenticalToRetainedSource && removal.ExpectedState.Fingerprint != retained?.SourceState.Fingerprint)
                AddCoverage(issues, removal.RecordId, removal.Format, "Exact-binary removal evidence does not match the retained source.");
        }

        HashSet<(CalibreBookId, string, string)> classified = retainedAssociations.Concat(removedAssociations).ToHashSet();
        if (!classified.SetEquals(expected.Keys)
            || retainedAssociations.Intersect(removedAssociations).Any(key => !definition.FormatRemovals.Any(removal =>
                removal.RecordId == key.Item1 && removal.Format == key.Item2 && removal.RelativePath == key.Item3
                && removal.Reason == FormatRemovalReason.RemovedWithSourceRecordAfterRetention)))
            AddCoverage(issues, null, null, "Every declared group format must be covered exactly once by retention or descriptive removal.");

        ValidateProvenance(definition, issues);

        foreach (ExpectedRecordState record in definition.ExpectedLibraryState.Records)
        {
            bool metadata = definition.BackupRequirements.Any(value => value.Kind == BackupRequirementKind.RecordMetadataSnapshot && value.RecordId == record.RecordId);
            if (!metadata) AddCoverage(issues, record.RecordId, null, "Every involved record requires a metadata backup.");
            if (record.HasCover && !definition.BackupRequirements.Any(value => value.Kind == BackupRequirementKind.CoverIfPresent && value.RecordId == record.RecordId))
                AddCoverage(issues, record.RecordId, null, "Every reported cover requires a cover backup.");
            foreach (ExpectedFormatState format in record.Formats)
            {
                bool file = definition.BackupRequirements.Any(value => value.Kind == BackupRequirementKind.FormatFile
                    && value.RecordId == record.RecordId && value.Format == format.Format);
                bool facts = definition.BackupRequirements.Any(value => value.Kind == BackupRequirementKind.ManagedPathAndFileState
                    && value.RecordId == record.RecordId && value.Format == format.Format);
                if (!file || !facts) AddCoverage(issues, record.RecordId, format.Format, "Every affected format requires file and managed-state backups.");
            }
        }

        if (!definition.BackupRequirements.Any(value => value.Kind == BackupRequirementKind.CleanupPlanArtifact)
            || !definition.BackupRequirements.Any(value => value.Kind == BackupRequirementKind.ExecutionAudit))
            AddCoverage(issues, null, null, "The plan artifact and future execution audit are mandatory backup requirements.");
        if (definition.RecordRemovals.Any(value => value.RecordId == definition.TargetRecordId)
            || definition.RecordRemovals.Count != definition.InvolvedRecordIds.Count - 1)
            issues.Add(new("PLAN.TARGET_INVALID", CleanupPlanIssueSeverity.BlockingError, CleanupPlanIssueSubjectKind.Record, "The target must survive as the sole retained record.", definition.TargetRecordId));
        foreach (RecordRemovalInstruction removal in definition.RecordRemovals)
        {
            HashSet<string> requiredBackups = definition.BackupRequirements.Where(value => value.RecordId == removal.RecordId)
                .Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            HashSet<string> declaredBackups = removal.BackupRequirementIds.ToHashSet(StringComparer.Ordinal);
            HashSet<string> requiredRetentions = definition.FormatRetentions.Where(value => value.SourceState.RecordId == removal.RecordId)
                .Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            HashSet<string> declaredRetentions = removal.RequiredRetainedFormatInstructionIds.ToHashSet(StringComparer.Ordinal);
            if (!declaredBackups.SetEquals(requiredBackups) || !declaredRetentions.SetEquals(requiredRetentions))
                AddCoverage(issues, removal.RecordId, null, "A record removal does not declare its complete backup and retained-contribution dependencies.");
        }
        HashSet<CalibreBookId> involved = definition.InvolvedRecordIds.ToHashSet();
        if (definition.BackupRequirements.Any(value => value.RecordId is not null && !involved.Contains(value.RecordId.Value)))
            AddCoverage(issues, null, null, "A backup requirement references a record outside the duplicate group.");
        return issues;
    }

    private static void ValidateProvenance(CleanupPlanDefinition definition, List<CleanupPlanIssue> issues)
    {
        ExpectedLibraryState expected = definition.ExpectedLibraryState;
        CleanupPlanProvenance provenance = definition.Provenance;
        HashSet<CalibreBookId> involved = definition.InvolvedRecordIds.ToHashSet();
        if (provenance.GroupId != expected.GroupId
            || provenance.RecommendationModelVersion != expected.RecommendationModelVersion
            || provenance.RecommendationInputVersion != expected.RecommendationInputVersion
            || provenance.ReviewedMetadataSourceRecordId != definition.TargetRecordId
            || provenance.UserOverride.RequestedStatus != provenance.ReviewStatus
            || !involved.Contains(provenance.GeneratedMetadataSourceRecordId)
            || provenance.GeneratedFormatSelections.Any(value => value.CandidateRecordIds.Any(id => !involved.Contains(id)))
            || provenance.ReviewedFormatSelections.Any(value => value.CandidateRecordIds.Any(id => !involved.Contains(id))))
        {
            AddCoverage(issues, null, null, "Cleanup-plan provenance does not match the expected input, target, or involved records.");
        }

        Dictionary<string, FormatRetentionInstruction> retained = definition.FormatRetentions.ToDictionary(value => value.Format, StringComparer.Ordinal);
        Dictionary<string, CalibreBookId[]> expectedCandidates = expected.Records.SelectMany(value => value.Formats)
            .GroupBy(value => value.Format, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(value => value.RecordId).Distinct()
                .OrderBy(value => value.Value).ToArray(), StringComparer.Ordinal);
        CleanupPlanFormatSelectionProvenance[] generated = provenance.GeneratedFormatSelections.ToArray();
        CleanupPlanFormatSelectionProvenance[] reviewed = provenance.ReviewedFormatSelections.ToArray();
        if (generated.Length != expectedCandidates.Count
            || generated.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != generated.Length
            || generated.Any(value => !expectedCandidates.TryGetValue(value.Format, out CalibreBookId[]? candidates)
                || !value.CandidateRecordIds.SequenceEqual(candidates))
            || reviewed.Length != retained.Count
            || reviewed.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != reviewed.Length
            || reviewed.Any(value => value.ResolutionStatus != FormatResolutionStatus.Selected
                || value.SelectedRecordId is null
                || !retained.TryGetValue(value.Format, out FormatRetentionInstruction? instruction)
                || instruction.SourceState.RecordId != value.SelectedRecordId.Value
                || !value.CandidateRecordIds.Contains(value.SelectedRecordId.Value)))
        {
            AddCoverage(issues, null, null, "Reviewed provenance does not exactly match retained format instructions.");
        }
    }

    private static void AddCoverage(List<CleanupPlanIssue> issues, CalibreBookId? recordId, string? format, string explanation) =>
        issues.Add(new("PLAN.SAFETY_COVERAGE_INCOMPLETE", CleanupPlanIssueSeverity.BlockingError, CleanupPlanIssueSubjectKind.Backup, explanation, recordId, format));
}
