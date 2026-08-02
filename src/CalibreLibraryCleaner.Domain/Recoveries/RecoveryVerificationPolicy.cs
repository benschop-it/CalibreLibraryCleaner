using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public sealed record RecoveryVerificationResult
{
    public RecoveryVerificationResult(
        IEnumerable<RecoveryIssue> issues,
        IEnumerable<RecoveryRecordIdMapping> recordIdMappings,
        DateTimeOffset verifiedAtUtc)
    {
        Issues = new RecoveryEligibilityResult(issues, verifiedAtUtc).Issues;
        RecordIdMappings = Array.AsReadOnly(recordIdMappings
            .OrderBy(value => value.LogicalRecordId.Value, StringComparer.Ordinal).ToArray());
        if (RecordIdMappings.Select(value => value.LogicalRecordId).Distinct().Count() != RecordIdMappings.Count
            || RecordIdMappings.Select(value => value.RecoveredRecordId).Distinct().Count() != RecordIdMappings.Count)
            throw new ArgumentException("Recovery verification contains ambiguous record-ID mappings.", nameof(recordIdMappings));
        VerifiedAtUtc = verifiedAtUtc.ToUniversalTime();
    }

    public IReadOnlyList<RecoveryIssue> Issues { get; }
    public IReadOnlyList<RecoveryRecordIdMapping> RecordIdMappings { get; }
    public DateTimeOffset VerifiedAtUtc { get; }
    public bool IsVerified => Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking)
        && Issues.All(value => value.Code != "RECOVERY.MANUAL_INTERVENTION_REQUIRED");
}

public static class RecoveryVerificationPolicy
{
    public static RecoveryVerificationResult VerifyFinalState(
        ExpectedRecoveredState expected,
        LibrarySnapshot snapshot,
        IEnumerable<RecoveryRecordIdMapping> mappings,
        DateTimeOffset verifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(snapshot);
        RecoveryRecordIdMapping[] mappingValues = mappings.ToArray();
        List<RecoveryIssue> issues = [];
        Dictionary<LogicalRecoveryRecordId, RecoveryRecordIdMapping> byLogical =
            mappingValues.ToDictionary(value => value.LogicalRecordId);
        Dictionary<CalibreBookId, CalibreBook> current = snapshot.Books.ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, HashSet<string>> allowedFormatsByRecord = [];
        Dictionary<CalibreBookId, LogicalRecoveryRecordId> logicalByRecord = [];

        foreach (ExpectedRecoveredRecordState record in expected.Records)
        {
            CalibreBookId? resolvedId = record.ExpectedCurrentRecordId;
            if (byLogical.TryGetValue(record.LogicalRecordId, out RecoveryRecordIdMapping? mapping))
                resolvedId = mapping.RecoveredRecordId;
            if (resolvedId is null || !current.TryGetValue(resolvedId.Value, out CalibreBook? actual))
            {
                Block(issues, "RECOVERY.EXPECTED_RECORD_MISSING", "An expected logical record does not exist exactly once.",
                    record.LogicalRecordId, resolvedId);
                continue;
            }
            HashSet<string> allowedFormats = allowedFormatsByRecord.GetValueOrDefault(actual.Id)
                ?? [];
            allowedFormats.UnionWith(record.OriginalState.Formats.Select(value => value.Format));
            allowedFormatsByRecord[actual.Id] = allowedFormats;
            logicalByRecord[actual.Id] = record.LogicalRecordId;
            if (!MetadataMatches(record.OriginalState, actual, record.SupportedMetadataFields))
                Block(issues, "RECOVERY.METADATA_MISMATCH", "Supported restored metadata does not match the verified pre-state.",
                    record.LogicalRecordId, actual.Id);
            foreach (ExpectedFormatState expectedFormat in record.OriginalState.Formats)
            {
                BookFormat? format = actual.Formats.SingleOrDefault(value => value.Format == expectedFormat.Format);
                if (format is null
                    || format.FileStatus is not (FormatFileStatus.Present or FormatFileStatus.ProjectedPresent)
                    || format.Fingerprint != expectedFormat.Fingerprint)
                    Block(issues, "RECOVERY.FORMAT_MISMATCH", "A restored format is missing or does not match the original backup hash.",
                        record.LogicalRecordId, actual.Id, expectedFormat.Format);
            }
        }

        foreach (PreservedContentExpectation preserved in expected.PreservedContent)
        {
            HashSet<string> allowedFormats =
                allowedFormatsByRecord.GetValueOrDefault(preserved.CurrentRecordId) ?? [];
            allowedFormats.Add(preserved.Format);
            allowedFormatsByRecord[preserved.CurrentRecordId] = allowedFormats;
            logicalByRecord.TryAdd(preserved.CurrentRecordId, preserved.LogicalRecordId);
            if (!current.TryGetValue(preserved.CurrentRecordId, out CalibreBook? record)
                || record.Formats.SingleOrDefault(value => value.Format == preserved.Format)
                    is not { FileStatus: FormatFileStatus.Present, Fingerprint: not null } format
                || format.Fingerprint != preserved.Fingerprint)
                Block(issues, "RECOVERY.PRESERVED_CONTENT_MISSING",
                    "Unexpected current content selected for preservation is missing or changed.",
                    preserved.LogicalRecordId, preserved.CurrentRecordId, preserved.Format);
        }
        foreach ((CalibreBookId recordId, string format) in expected.ExpectedAbsentFormats)
        {
            if (current.TryGetValue(recordId, out CalibreBook? record)
                && record.Formats.Any(value => value.Format == format))
                Block(issues, "RECOVERY.CLEANUP_ONLY_FORMAT_REMAINS",
                    "An explicitly approved cleanup-only format remains after destructive recovery.",
                    null, recordId, format);
        }
        foreach (CalibreBookId recordId in expected.ExpectedAbsentRecords)
        {
            if (current.ContainsKey(recordId))
                Block(issues, "RECOVERY.CLEANUP_ONLY_RECORD_REMAINS",
                    "An explicitly approved cleanup-only record remains after destructive recovery.",
                    null, recordId);
        }
        foreach ((CalibreBookId recordId, HashSet<string> allowedFormats) in
                 allowedFormatsByRecord)
        {
            if (!current.TryGetValue(recordId, out CalibreBook? record)) continue;
            foreach (BookFormat unexpected in record.Formats.Where(value =>
                         !allowedFormats.Contains(value.Format)))
                Block(issues, "RECOVERY.UNEXPECTED_AFFECTED_FORMAT",
                    "An affected record contains a format that was neither approved as recovered state nor selected for preservation.",
                    logicalByRecord.GetValueOrDefault(recordId), recordId,
                    unexpected.Format);
        }
        return new(issues, mappingValues, verifiedAtUtc);
    }

    private static bool MetadataMatches(
        ExpectedRecordState expected,
        CalibreBook actual,
        IReadOnlyList<string> supportedFields)
    {
        HashSet<string> fields = supportedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        BookPublicationMetadata publication = actual.PublicationMetadata;
        return (!fields.Contains("title") || expected.Title == actual.Title)
            && (!fields.Contains("authors") || expected.Authors.Select(value => (value.Name, value.SortName))
                .SequenceEqual(actual.Authors.Select(value => (value.Name, value.SortName))))
            && (!fields.Contains("author_sort") || expected.AuthorSort == actual.AuthorSort)
            && (!fields.Contains("identifiers") || expected.Identifiers
                .SequenceEqual(actual.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                    .ThenBy(value => value.Value, StringComparer.Ordinal)
                    .Select(value => new ExpectedIdentifierState(value.Type, value.Value))))
            && (!fields.Contains("publisher") || expected.Publisher == publication.Publisher)
            && (!fields.Contains("pubdate") || expected.PublicationDate == publication.PublicationDate)
            && (!fields.Contains("series") || expected.Series == publication.Series)
            && (!fields.Contains("series_index") || expected.SeriesIndex == publication.SeriesIndex)
            && (!fields.Contains("languages") || expected.Languages.SequenceEqual(publication.Languages))
            && (!fields.Contains("cover") || expected.HasCover == publication.HasCover);
    }

    private static void Block(
        List<RecoveryIssue> issues,
        string code,
        string explanation,
        LogicalRecoveryRecordId? logicalRecordId,
        CalibreBookId? currentRecordId,
        string? format = null) =>
        issues.Add(new(code, RecoveryIssueSeverity.Blocking, "Final verification", explanation,
            logicalRecordId, currentRecordId, format));
}
