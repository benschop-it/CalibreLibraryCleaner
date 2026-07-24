using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Executions;

public static class ExecutionSnapshotDigestPolicy
{
    public static Sha256Digest ComputeUnaffected(LibrarySnapshot snapshot, IEnumerable<CalibreBookId> involvedRecordIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<CalibreBookId> involved = involvedRecordIds.ToHashSet();
        StringBuilder canonical = new();
        Add(canonical, snapshot.Identity.CalibreLibraryUuid);
        Add(canonical, snapshot.Identity.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        foreach (CalibreBook book in snapshot.Books.Where(value => !involved.Contains(value.Id)).OrderBy(value => value.Id.Value))
            AddBook(canonical, book);
        foreach (Findings.LibraryFinding finding in snapshot.Findings.Where(value => value.BookId is null || !involved.Contains(value.BookId.Value))
                     .OrderBy(value => value.BookId?.Value ?? 0).ThenBy(value => value.Code, StringComparer.Ordinal)
                     .ThenBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            Add(canonical, finding.BookId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, finding.Code);
            Add(canonical, finding.Format ?? string.Empty);
            Add(canonical, finding.RelativePath ?? string.Empty);
        }

        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
    }

    private static void AddBook(StringBuilder canonical, CalibreBook book)
    {
        Add(canonical, book.Id.Value.ToString(CultureInfo.InvariantCulture));
        Add(canonical, book.Title);
        Add(canonical, book.AuthorSort);
        foreach (BookAuthor author in book.Authors) { Add(canonical, author.Id.Value.ToString(CultureInfo.InvariantCulture)); Add(canonical, author.Name); Add(canonical, author.SortName); }
        foreach (BookIdentifier identifier in book.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal).ThenBy(value => value.Value, StringComparer.Ordinal)) { Add(canonical, identifier.Type); Add(canonical, identifier.Value); }
        BookPublicationMetadata publication = book.PublicationMetadata;
        Add(canonical, publication.Publisher ?? string.Empty);
        Add(canonical, publication.PublicationDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        Add(canonical, publication.Series ?? string.Empty);
        Add(canonical, publication.SeriesIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        foreach (string language in publication.Languages) Add(canonical, language);
        Add(canonical, publication.HasCover ? "1" : "0");
        Add(canonical, book.RelativeDirectory.Replace('\\', '/'));
        foreach (BookFormat format in book.Formats.OrderBy(value => value.Format, StringComparer.Ordinal))
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
    }

    private static void Add(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value).Append(';');
}

public static class CleanupExecutionVerificationPolicy
{
    public static ExecutionVerificationResult VerifyConstructiveState(
        CleanupPlan plan,
        LibrarySnapshot snapshot,
        IEnumerable<string> processedRetentionInstructionIds,
        IEnumerable<CalibreBookId> removedRecordIds,
        Sha256Digest unaffectedBaseline,
        DateTimeOffset verifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<string> processed = processedRetentionInstructionIds.ToHashSet(StringComparer.Ordinal);
        HashSet<CalibreBookId> removed = removedRecordIds.ToHashSet();
        List<ExecutionIssue> issues = [];
        ExpectedLibraryState expected = plan.Definition.ExpectedLibraryState;
        if (!string.Equals(expected.LibraryUuid, snapshot.Identity.CalibreLibraryUuid, StringComparison.Ordinal)
            || expected.SchemaVersion != snapshot.Identity.SchemaVersion)
            Block(issues, "EXECUTION.LIBRARY_IDENTITY_CHANGED", "The library identity or schema changed.");
        if (ExecutionSnapshotDigestPolicy.ComputeUnaffected(snapshot, plan.Definition.InvolvedRecordIds) != unaffectedBaseline)
            Block(issues, "EXECUTION.UNRELATED_STATE_CHANGED", "Library state outside the approved plan changed unexpectedly.");

        Dictionary<CalibreBookId, CalibreBook> current = snapshot.Books.ToDictionary(value => value.Id);
        ExpectedRecordState expectedTarget = expected.Records.Single(value => value.RecordId == plan.Definition.TargetRecordId);
        if (!current.TryGetValue(expectedTarget.RecordId, out CalibreBook? target))
            Block(issues, "EXECUTION.TARGET_MISSING", "The target record no longer exists.", expectedTarget.RecordId);
        else
        {
            if (!MetadataMatches(expectedTarget, target))
                Block(issues, "EXECUTION.TARGET_METADATA_CHANGED", "The target metadata, cover state, or managed directory changed unexpectedly.", target.Id);
            VerifyTargetFormats(plan, expectedTarget, target, processed, issues);
        }

        foreach (ExpectedRecordState record in expected.Records.Where(value => value.RecordId != expectedTarget.RecordId))
        {
            if (removed.Contains(record.RecordId))
            {
                if (current.ContainsKey(record.RecordId))
                    Block(issues, "EXECUTION.RECORD_NOT_REMOVED", "A planned source record is still present.", record.RecordId);
            }
            else if (!current.TryGetValue(record.RecordId, out CalibreBook? book) || !RecordMatches(record, book))
            {
                Block(issues, "EXECUTION.SOURCE_STATE_CHANGED", "A source record required by the remaining operations changed or disappeared.", record.RecordId);
            }
        }

        return new(issues, verifiedAtUtc);
    }

    public static ExecutionVerificationResult VerifyFinalState(
        CleanupPlan plan,
        LibrarySnapshot snapshot,
        Sha256Digest unaffectedBaseline,
        DateTimeOffset verifiedAtUtc) =>
        VerifyConstructiveState(plan, snapshot, plan.Definition.FormatRetentions.Select(value => value.Id),
            plan.Definition.RecordRemovals.Select(value => value.RecordId), unaffectedBaseline, verifiedAtUtc);

    private static void VerifyTargetFormats(
        CleanupPlan plan,
        ExpectedRecordState expectedTarget,
        CalibreBook target,
        HashSet<string> processed,
        List<ExecutionIssue> issues)
    {
        Dictionary<string, BookFormat> current = target.Formats.GroupBy(value => value.Format, StringComparer.Ordinal)
            .Where(value => value.Count() == 1).ToDictionary(value => value.Key, value => value.Single(), StringComparer.Ordinal);
        if (current.Count != target.Formats.Count)
            Block(issues, "EXECUTION.TARGET_FORMAT_DUPLICATE", "The target contains an ambiguous duplicate canonical format.", target.Id);
        Dictionary<string, ExpectedFormatState> original = expectedTarget.Formats.ToDictionary(value => value.Format, StringComparer.Ordinal);
        HashSet<string> expectedInventory = original.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (FormatRetentionInstruction retention in plan.Definition.FormatRetentions)
        {
            bool shouldHaveSelected = retention.Mode == FormatRetentionMode.RetainInTarget || processed.Contains(retention.Id);
            if (shouldHaveSelected) expectedInventory.Add(retention.Format);
            if (!current.TryGetValue(retention.Format, out BookFormat? actual))
            {
                if (shouldHaveSelected || original.ContainsKey(retention.Format))
                    Block(issues, "EXECUTION.RETAINED_FORMAT_MISSING", "A required target format is missing.", target.Id, retention.Format);
                continue;
            }

            FormatFileFingerprint expectedFingerprint = shouldHaveSelected
                ? retention.SourceState.Fingerprint
                : original[retention.Format].Fingerprint;
            if (actual.FileStatus != FormatFileStatus.Present || actual.Fingerprint != expectedFingerprint)
                Block(issues, "EXECUTION.RETAINED_FORMAT_MISMATCH", "A target format does not match the selected source bytes.", target.Id, retention.Format);
            if (retention.Mode == FormatRetentionMode.RetainInTarget
                && (!original.TryGetValue(retention.Format, out ExpectedFormatState? preserved) || !FormatMatches(preserved, actual)))
                Block(issues, "EXECUTION.PRESERVED_FORMAT_CHANGED", "A target format declared as preserved changed unexpectedly.", target.Id, retention.Format);
        }

        if (!current.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedInventory))
            Block(issues, "EXECUTION.TARGET_FORMAT_INVENTORY_CHANGED", "The target format inventory contains a missing or unexpected format.", target.Id);
    }

    private static bool RecordMatches(ExpectedRecordState expected, CalibreBook current) =>
        MetadataMatches(expected, current)
        && expected.Formats.Count == current.Formats.Count
        && expected.Formats.All(format => current.Formats.SingleOrDefault(value => value.Format == format.Format) is { } actual && FormatMatches(format, actual));

    private static bool MetadataMatches(ExpectedRecordState expected, CalibreBook current)
    {
        BookPublicationMetadata publication = current.PublicationMetadata;
        return expected.RecordId == current.Id
            && string.Equals(expected.Title, current.Title, StringComparison.Ordinal)
            && string.Equals(expected.AuthorSort, current.AuthorSort, StringComparison.Ordinal)
            && expected.Authors.SequenceEqual(current.Authors.Select(value => new ExpectedAuthorState(value.Id, value.Name, value.SortName)))
            && expected.Identifiers.SequenceEqual(current.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                .ThenBy(value => value.Value, StringComparer.Ordinal).Select(value => new ExpectedIdentifierState(value.Type, value.Value)))
            && string.Equals(expected.Publisher, publication.Publisher, StringComparison.Ordinal)
            && expected.PublicationDate == publication.PublicationDate?.ToUniversalTime()
            && string.Equals(expected.Series, publication.Series, StringComparison.Ordinal)
            && expected.SeriesIndex == publication.SeriesIndex
            && expected.Languages.SequenceEqual(publication.Languages)
            && expected.HasCover == publication.HasCover
            && string.Equals(expected.RelativeDirectory, current.RelativeDirectory.Replace('\\', '/'), StringComparison.Ordinal);
    }

    private static bool FormatMatches(ExpectedFormatState expected, BookFormat current) =>
        expected.Format == current.Format
        && expected.StoredFileName == current.StoredFileName
        && expected.RelativePath == current.ExpectedRelativePath.Replace('\\', '/')
        && expected.Status == current.FileStatus
        && expected.Fingerprint == current.Fingerprint
        && expected.Observation == current.Observation;

    private static void Block(List<ExecutionIssue> issues, string code, string explanation, CalibreBookId? recordId = null, string? format = null) =>
        issues.Add(new(code, ExecutionIssueSeverity.BlockingError, explanation, recordId, format));
}
