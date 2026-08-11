using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public enum PostExactAssociationDisposition
{
    Unchanged,
    Transferred,
    ExpectedRemoved,
    TargetedHashRequired,
    PreservedInvalidPath,
    Unexplained,
}

public readonly record struct PostExactAssociationKey
{
    public PostExactAssociationKey(CalibreBookId bookId, string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        BookId = bookId;
        Format = format.Trim().ToUpperInvariant();
    }

    public CalibreBookId BookId { get; }
    public string Format { get; }
}

public sealed record PostExactAssociationDecision(
    PostExactAssociationKey Association,
    PostExactAssociationDisposition Disposition,
    FormatFileFingerprint? ExpectedFingerprint,
    bool IsTransferTarget = false);

public sealed record PostExactReconciliationIssue(string Code, string Explanation);

public sealed record PostExactReconciliationResult
{
    public PostExactReconciliationResult(
        IEnumerable<PostExactAssociationDecision> decisions,
        IEnumerable<PostExactReconciliationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(issues);
        Decisions = new ReadOnlyCollection<PostExactAssociationDecision>(decisions
            .OrderBy(value => value.Association.BookId.Value)
            .ThenBy(value => value.Association.Format, StringComparer.Ordinal)
            .ThenBy(value => value.Disposition)
            .ToArray());
        Issues = new ReadOnlyCollection<PostExactReconciliationIssue>(issues.ToArray());
    }

    public IReadOnlyList<PostExactAssociationDecision> Decisions { get; }
    public IReadOnlyList<PostExactReconciliationIssue> Issues { get; }
    public bool IsSuccess => Issues.Count == 0
        && Decisions.All(value => value.Disposition != PostExactAssociationDisposition.Unexplained);
}

public sealed record PostExactReconciliationOptions(bool RequireTransferTargetHash = true);

public static class PostExactCleanupReconciliationPolicy
{
    public static PostExactReconciliationResult Reconcile(
        LibraryState preExactState,
        LibraryState postExactState,
        IReadOnlyList<LibraryStateDelta> completedExactDeltas,
        CalibreCatalogRecord freshCatalog,
        PostExactReconciliationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(preExactState);
        ArgumentNullException.ThrowIfNull(postExactState);
        ArgumentNullException.ThrowIfNull(completedExactDeltas);
        ArgumentNullException.ThrowIfNull(freshCatalog);
        options ??= new();
        List<PostExactReconciliationIssue> issues = [];
        List<PostExactAssociationDecision> decisions = [];

        if (!ValidateStateBindings(preExactState, postExactState, completedExactDeltas, issues))
            return new(decisions, issues);

        LibraryState replayed;
        try
        {
            replayed = completedExactDeltas.Count == 0
                ? preExactState
                : LibraryStateDeltaPolicy.ApplyBatch(preExactState, completedExactDeltas);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            issues.Add(new("POST_EXACT.DELTA_REPLAY_FAILED", exception.Message));
            return new(decisions, issues);
        }

        if (!SnapshotsDescribeSameInventory(replayed.Snapshot, postExactState.Snapshot))
        {
            issues.Add(new(
                "POST_EXACT.PROJECTED_STATE_MISMATCH",
                "Completed Exact deltas do not reproduce the authoritative projected inventory."));
            return new(decisions, issues);
        }

        if (!string.Equals(freshCatalog.LibraryUuid, postExactState.Snapshot.Identity.CalibreLibraryUuid,
                StringComparison.Ordinal)
            || freshCatalog.SchemaVersion != postExactState.Snapshot.Identity.SchemaVersion)
        {
            issues.Add(new(
                "POST_EXACT.LIBRARY_IDENTITY_CHANGED",
                "The refreshed catalog identity does not match authoritative state."));
            return new(decisions, issues);
        }

        Dictionary<CalibreBookId, CalibreBook> preBooks = preExactState.Snapshot.Books
            .ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, CalibreBook> expectedBooks = postExactState.Snapshot.Books
            .ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, CalibreBookRecord> catalogBooks;
        try
        {
            catalogBooks = freshCatalog.Books.ToDictionary(value => new CalibreBookId(value.Id));
        }
        catch (ArgumentException)
        {
            issues.Add(new("POST_EXACT.DUPLICATE_CATALOG_RECORD", "The refreshed catalog contains duplicate record IDs."));
            return new(decisions, issues);
        }

        AddRecordSetIssues(expectedBooks.Keys, catalogBooks.Keys, issues);
        HashSet<PostExactAssociationKey> transferTargets = completedExactDeltas
            .OfType<AddOrReplaceFormatLibraryStateDelta>()
            .Select(value => new PostExactAssociationKey(value.RecordId, value.Format))
            .ToHashSet();
        Dictionary<PostExactAssociationKey, FormatFileFingerprint> transferFingerprints;
        try
        {
            transferFingerprints = completedExactDeltas
                .OfType<AddOrReplaceFormatLibraryStateDelta>()
                .ToDictionary(
                    value => new PostExactAssociationKey(value.RecordId, value.Format),
                    value => value.Fingerprint);
        }
        catch (ArgumentException)
        {
            issues.Add(new(
                "POST_EXACT.DUPLICATE_TRANSFER_TARGET",
                "Completed Exact evidence contains duplicate transfer targets."));
            return new(decisions, issues);
        }
        foreach (AddOrReplaceFormatLibraryStateDelta transfer in completedExactDeltas
                     .OfType<AddOrReplaceFormatLibraryStateDelta>())
        {
            bool hasSourceRemoval = transfer.ExpectedPreviousFingerprint is null
                && completedExactDeltas.OfType<RemoveFormatLibraryStateDelta>().Any(removal =>
                    removal.RecordId != transfer.RecordId
                    && removal.Format == transfer.Format
                    && removal.ExpectedFingerprint == transfer.Fingerprint
                    && preBooks.TryGetValue(removal.RecordId, out CalibreBook? sourceBook)
                    && sourceBook.Formats.Any(format =>
                        format.Format == transfer.Format
                        && format.FileStatus == FormatFileStatus.Present
                        && format.Fingerprint == transfer.Fingerprint
                        && format.Observation is not null));
            if (!hasSourceRemoval)
            {
                issues.Add(new(
                    "POST_EXACT.TRANSFER_SOURCE_UNPROVEN",
                    $"Record {transfer.RecordId.Value} has an unproven {transfer.Format} transfer."));
            }
        }
        if (issues.Count > 0) return new(decisions, issues);

        foreach ((CalibreBookId bookId, CalibreBook expectedBook) in expectedBooks.OrderBy(value => value.Key.Value))
        {
            if (!catalogBooks.TryGetValue(bookId, out CalibreBookRecord? catalogBook)) continue;
            if (!MetadataMatches(expectedBook, catalogBook))
            {
                issues.Add(new(
                    "POST_EXACT.METADATA_CHANGED",
                    $"Catalog metadata differs for record {bookId.Value}."));
            }

            Dictionary<string, CalibreFormatRecord>? catalogFormats = CanonicalFormats(catalogBook, issues);
            if (catalogFormats is null) continue;
            Dictionary<string, BookFormat> expectedFormats = expectedBook.Formats
                .ToDictionary(value => value.Format, StringComparer.Ordinal);
            foreach (string unexpected in catalogFormats.Keys.Except(expectedFormats.Keys, StringComparer.Ordinal))
            {
                decisions.Add(Unexplained(bookId, unexpected));
                issues.Add(new(
                    "POST_EXACT.UNEXPECTED_FORMAT",
                    $"Record {bookId.Value} contains an unexplained {unexpected} association."));
            }
            foreach (string missing in expectedFormats.Keys.Except(catalogFormats.Keys, StringComparer.Ordinal))
            {
                decisions.Add(Unexplained(bookId, missing));
                issues.Add(new(
                    "POST_EXACT.MISSING_FORMAT",
                    $"Record {bookId.Value} is missing expected {missing} association."));
            }

            foreach (string format in expectedFormats.Keys.Intersect(catalogFormats.Keys, StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                PostExactAssociationKey key = new(bookId, format);
                BookFormat expectedFormat = expectedFormats[format];
                if (transferTargets.Contains(key))
                {
                    FormatFileFingerprint fingerprint = transferFingerprints[key];
                    decisions.Add(new(
                        key,
                        options.RequireTransferTargetHash
                            ? PostExactAssociationDisposition.TargetedHashRequired
                            : PostExactAssociationDisposition.Transferred,
                        fingerprint,
                        IsTransferTarget: true));
                    continue;
                }

                BookFormat? preFormat = preBooks.GetValueOrDefault(bookId)?.Formats.SingleOrDefault(
                    value => string.Equals(value.Format, format, StringComparison.Ordinal));
                CalibreFormatRecord catalogFormat = catalogFormats[format];
                if (preFormat is
                    {
                        FileStatus: FormatFileStatus.InvalidPath,
                        Fingerprint: null,
                        Observation: null,
                    }
                    && expectedFormat.FileStatus == FormatFileStatus.InvalidPath
                    && expectedFormat.Fingerprint is null
                    && expectedFormat.Observation is null
                    && string.Equals(preFormat.StoredFileName, expectedFormat.StoredFileName,
                        StringComparison.Ordinal)
                    && string.Equals(preFormat.StoredFileName, catalogFormat.StoredName,
                        StringComparison.Ordinal))
                {
                    decisions.Add(new(
                        key,
                        PostExactAssociationDisposition.PreservedInvalidPath,
                        null));
                    continue;
                }
                if (preFormat is null
                    || preFormat.FileStatus != FormatFileStatus.Present
                    || preFormat.Fingerprint is null
                    || preFormat.Observation is null
                    || expectedFormat.Fingerprint != preFormat.Fingerprint
                    || !string.Equals(preFormat.StoredFileName, catalogFormat.StoredName, StringComparison.Ordinal))
                {
                    decisions.Add(Unexplained(bookId, format));
                    issues.Add(new(
                        "POST_EXACT.ASSOCIATION_CHANGED",
                        $"Record {bookId.Value} has an unexplained {format} association change."));
                    continue;
                }

                decisions.Add(new(
                    key,
                    PostExactAssociationDisposition.Unchanged,
                    preFormat.Fingerprint));
            }
        }

        foreach (RemoveFormatLibraryStateDelta removed in completedExactDeltas
                     .OfType<RemoveFormatLibraryStateDelta>())
        {
            PostExactAssociationKey key = new(removed.RecordId, removed.Format);
            if (catalogBooks.TryGetValue(removed.RecordId, out CalibreBookRecord? catalogBook)
                && catalogBook.Formats.Any(value =>
                    string.Equals(value.Format.Trim(), removed.Format, StringComparison.OrdinalIgnoreCase)))
            {
                decisions.Add(Unexplained(removed.RecordId, removed.Format));
                issues.Add(new(
                    "POST_EXACT.REMOVED_FORMAT_PRESENT",
                    $"Record {removed.RecordId.Value} still contains removed {removed.Format} association."));
            }
            else if (!postExactState.Snapshot.Books.Any(value => value.Id == removed.RecordId
                         && value.Formats.Any(format => format.Format == removed.Format)))
            {
                decisions.Add(new(
                    key,
                    PostExactAssociationDisposition.ExpectedRemoved,
                    removed.ExpectedFingerprint));
            }
        }

        return new(decisions, issues);
    }

    private static bool ValidateStateBindings(
        LibraryState preExactState,
        LibraryState postExactState,
        IReadOnlyList<LibraryStateDelta> deltas,
        List<PostExactReconciliationIssue> issues)
    {
        if (!preExactState.IsAuthoritative || !postExactState.IsAuthoritative
            || preExactState.GenerationId != postExactState.GenerationId
            || preExactState.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.ExactReady
            || postExactState.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.CandidatePreparationReady
            || postExactState.WorkflowCheckpoint.Source is not null
            || !postExactState.IsWorkflowCheckpointCurrent)
        {
            issues.Add(new(
                "POST_EXACT.WORKFLOW_STATE_INVALID",
                "Post-Exact reconciliation requires compatible authoritative Exact workflow state."));
            return false;
        }

        if (deltas.Any(value => value is not RemoveFormatLibraryStateDelta
                and not AddOrReplaceFormatLibraryStateDelta
                and not RemoveRecordLibraryStateDelta)
            || deltas.Select((value, index) => value.GenerationId == preExactState.GenerationId
                    && value.ExpectedRevision.Value == checked(preExactState.Revision.Value + index))
                .Any(value => !value)
            || checked(preExactState.Revision.Value + deltas.Count) != postExactState.Revision.Value)
        {
            issues.Add(new(
                "POST_EXACT.DELTA_SEQUENCE_INVALID",
                "Completed mutation evidence is not a contiguous Exact cleanup delta sequence."));
            return false;
        }

        return true;
    }

    private static void AddRecordSetIssues(
        IEnumerable<CalibreBookId> expected,
        IEnumerable<CalibreBookId> actual,
        List<PostExactReconciliationIssue> issues)
    {
        HashSet<CalibreBookId> expectedSet = expected.ToHashSet();
        HashSet<CalibreBookId> actualSet = actual.ToHashSet();
        foreach (CalibreBookId value in actualSet.Except(expectedSet).OrderBy(value => value.Value))
            issues.Add(new("POST_EXACT.UNEXPECTED_RECORD", $"Catalog record {value.Value} is unexplained."));
        foreach (CalibreBookId value in expectedSet.Except(actualSet).OrderBy(value => value.Value))
            issues.Add(new("POST_EXACT.MISSING_RECORD", $"Expected catalog record {value.Value} is missing."));
    }

    private static Dictionary<string, CalibreFormatRecord>? CanonicalFormats(
        CalibreBookRecord book,
        List<PostExactReconciliationIssue> issues)
    {
        try
        {
            return book.Formats.ToDictionary(
                value => CanonicalFormat(value.Format),
                StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            issues.Add(new(
                "POST_EXACT.DUPLICATE_FORMAT_LABEL",
                $"Catalog record {book.Id} contains duplicate canonical format labels."));
            return null;
        }
    }

    private static string CanonicalFormat(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToUpperInvariant();
    }

    private static bool MetadataMatches(CalibreBook expected, CalibreBookRecord actual)
    {
        if (!string.Equals(expected.Title, actual.Title, StringComparison.Ordinal)
            || !string.Equals(expected.AuthorSort, actual.AuthorSort, StringComparison.Ordinal)
            || !string.Equals(NormalizePath(expected.RelativeDirectory), NormalizePath(actual.RelativeDirectory),
                StringComparison.Ordinal)
            || !expected.Authors.Select(value => (value.Id?.Value, value.Name, value.SortName))
                .SequenceEqual(actual.Authors.Select(value => ((long?)value.Id, value.Name, value.SortName)))
            || !expected.Identifiers.Select(value => (value.Type, value.Value))
                .OrderBy(value => value.Type, StringComparer.Ordinal)
                .ThenBy(value => value.Value, StringComparer.Ordinal)
                .SequenceEqual(actual.Identifiers.Select(value => (value.Type, value.Value))
                    .OrderBy(value => value.Type, StringComparer.Ordinal)
                    .ThenBy(value => value.Value, StringComparer.Ordinal)))
        {
            return false;
        }

        CalibrePublicationRecord? publication = actual.Publication;
        return string.Equals(expected.PublicationMetadata.Publisher, publication?.Publisher, StringComparison.Ordinal)
            && expected.PublicationMetadata.PublicationDate == publication?.PublicationDate
            && string.Equals(expected.PublicationMetadata.Series, publication?.Series, StringComparison.Ordinal)
            && expected.PublicationMetadata.SeriesIndex == publication?.SeriesIndex
            && expected.PublicationMetadata.Languages.SequenceEqual(publication?.Languages ?? [])
            && expected.PublicationMetadata.HasCover == (publication?.HasCover ?? false);
    }

    private static bool SnapshotsDescribeSameInventory(LibrarySnapshot left, LibrarySnapshot right)
    {
        if (left.Identity != right.Identity || left.Books.Count != right.Books.Count) return false;
        Dictionary<CalibreBookId, CalibreBook> rightBooks = right.Books.ToDictionary(value => value.Id);
        return left.Books.All(book => rightBooks.TryGetValue(book.Id, out CalibreBook? other)
            && MetadataMatches(book, ToCatalogBook(other))
            && book.Formats.Select(value => (value.Format, value.Fingerprint, value.FileStatus))
                .OrderBy(value => value.Format, StringComparer.Ordinal)
                .SequenceEqual(other.Formats.Select(value => (value.Format, value.Fingerprint, value.FileStatus))
                    .OrderBy(value => value.Format, StringComparer.Ordinal)));
    }

    private static CalibreBookRecord ToCatalogBook(CalibreBook book) => new(
        book.Id.Value,
        book.Title,
        book.AuthorSort,
        book.RelativeDirectory,
        book.Authors.Select(value => new CalibreAuthorRecord(value.Id!.Value.Value, value.Name, value.SortName)).ToArray(),
        book.Identifiers.Select(value => new CalibreIdentifierRecord(value.Type, value.Value)).ToArray(),
        book.Formats.Select(value => new CalibreFormatRecord(value.Format, value.StoredFileName)).ToArray(),
        new(
            book.PublicationMetadata.Publisher,
            book.PublicationMetadata.PublicationDate,
            book.PublicationMetadata.Series,
            book.PublicationMetadata.SeriesIndex,
            book.PublicationMetadata.Languages,
            book.PublicationMetadata.HasCover));

    private static PostExactAssociationDecision Unexplained(CalibreBookId bookId, string format) => new(
        new(bookId, format),
        PostExactAssociationDisposition.Unexplained,
        null);

    private static string NormalizePath(string value) => value.Replace('\\', '/');
}
