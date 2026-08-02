using CalibreLibraryCleaner.Domain.Duplicates;

namespace CalibreLibraryCleaner.Domain.Libraries;

public abstract record LibraryStateDelta
{
    protected LibraryStateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        string operationId,
        DateTimeOffset appliedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 256) throw new ArgumentException("A state-delta operation ID is too long.", nameof(operationId));
        GenerationId = generationId;
        ExpectedRevision = expectedRevision;
        OperationId = operationId.Trim();
        AppliedAtUtc = appliedAtUtc.ToUniversalTime();
    }

    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision ExpectedRevision { get; }
    public string OperationId { get; }
    public DateTimeOffset AppliedAtUtc { get; }
}

public sealed record RemoveFormatLibraryStateDelta : LibraryStateDelta
{
    public RemoveFormatLibraryStateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        string operationId,
        DateTimeOffset appliedAtUtc,
        CalibreBookId recordId,
        string format,
        FormatFileFingerprint expectedFingerprint)
        : base(generationId, expectedRevision, operationId, appliedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        RecordId = recordId;
        Format = format.Trim().ToUpperInvariant();
        ExpectedFingerprint = expectedFingerprint ?? throw new ArgumentNullException(nameof(expectedFingerprint));
    }

    public CalibreBookId RecordId { get; }
    public string Format { get; }
    public FormatFileFingerprint ExpectedFingerprint { get; }
}

public sealed record RemoveRecordLibraryStateDelta : LibraryStateDelta
{
    public RemoveRecordLibraryStateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        string operationId,
        DateTimeOffset appliedAtUtc,
        CalibreBookId recordId)
        : base(generationId, expectedRevision, operationId, appliedAtUtc) => RecordId = recordId;

    public CalibreBookId RecordId { get; }
}

public static class LibraryStateDeltaPolicy
{
    public static LibraryState Apply(LibraryState state, LibraryStateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(delta);
        if (!state.IsAuthoritative)
            throw new InvalidOperationException("Deltas cannot be applied while library state is uncertain.");
        if (delta.GenerationId != state.GenerationId)
            throw new InvalidOperationException("The delta belongs to a different library-state generation.");
        if (delta.ExpectedRevision != state.Revision)
            throw new InvalidOperationException("The delta expected a different library-state revision.");
        if (delta.AppliedAtUtc < state.ProjectedAtUtc)
            throw new InvalidOperationException("The delta predates the current projected state.");

        LibrarySnapshot projected = delta switch
        {
            RemoveFormatLibraryStateDelta removeFormat => RemoveFormat(state.Snapshot, removeFormat),
            RemoveRecordLibraryStateDelta removeRecord => RemoveRecord(state.Snapshot, removeRecord),
            _ => throw new ArgumentOutOfRangeException(nameof(delta), delta.GetType().Name, "Unsupported library-state delta."),
        };
        return new(state.GenerationId, state.Revision.Next(), LibraryStateStatus.Authoritative,
            projected, delta.AppliedAtUtc);
    }

    private static LibrarySnapshot RemoveFormat(
        LibrarySnapshot snapshot,
        RemoveFormatLibraryStateDelta delta)
    {
        CalibreBook current = snapshot.Books.SingleOrDefault(value => value.Id == delta.RecordId)
            ?? throw new InvalidOperationException("The format-removal record is not present in the authoritative state.");
        BookFormat removed = current.Formats.SingleOrDefault(value =>
            string.Equals(value.Format, delta.Format, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The format-removal association is not present in the authoritative state.");
        if (removed.Fingerprint != delta.ExpectedFingerprint)
            throw new InvalidOperationException("The format-removal fingerprint does not match authoritative state.");

        CalibreBook replacement = CopyBook(current, current.Formats.Where(value => value != removed));
        CalibreBook[] books = snapshot.Books.Select(value => value.Id == current.Id ? replacement : value).ToArray();
        ExactBinaryDuplicateGroup[] binaryGroups = FilterBinaryGroups(snapshot.ExactBinaryDuplicateGroups,
            member => member.BookId == delta.RecordId && string.Equals(member.Format, delta.Format, StringComparison.Ordinal));
        return Project(snapshot, books,
            snapshot.Findings.Where(value => value.BookId != delta.RecordId
                || !string.Equals(value.Format, delta.Format, StringComparison.Ordinal)),
            binaryGroups,
            snapshot.ExactMetadataDuplicateGroups,
            snapshot.EpubAssessments.Where(value => value.CalibreBookId != delta.RecordId
                || !string.Equals(value.Format, delta.Format, StringComparison.Ordinal)),
            snapshot.PdfAssessments.Where(value => value.CalibreBookId != delta.RecordId
                || !string.Equals(value.Format, delta.Format, StringComparison.Ordinal)));
    }

    private static LibrarySnapshot RemoveRecord(
        LibrarySnapshot snapshot,
        RemoveRecordLibraryStateDelta delta)
    {
        CalibreBook removed = snapshot.Books.SingleOrDefault(value => value.Id == delta.RecordId)
            ?? throw new InvalidOperationException("The record removal target is not present in the authoritative state.");
        if (removed.Formats.Count != 0)
            throw new InvalidOperationException("A record can be projected as removed only after all formats are absent.");

        CalibreBook[] books = snapshot.Books.Where(value => value.Id != delta.RecordId).ToArray();
        ExactBinaryDuplicateGroup[] binaryGroups = FilterBinaryGroups(snapshot.ExactBinaryDuplicateGroups,
            member => member.BookId == delta.RecordId);
        ExactMetadataDuplicateGroup[] metadataGroups = snapshot.ExactMetadataDuplicateGroups
            .Select(group => group.Members.Contains(delta.RecordId)
                ? CreateMetadataGroup(group, group.Members.Where(value => value != delta.RecordId))
                : group)
            .Where(value => value is not null)
            .Cast<ExactMetadataDuplicateGroup>()
            .ToArray();
        return Project(snapshot, books,
            snapshot.Findings.Where(value => value.BookId != delta.RecordId),
            binaryGroups,
            metadataGroups,
            snapshot.EpubAssessments.Where(value => value.CalibreBookId != delta.RecordId),
            snapshot.PdfAssessments.Where(value => value.CalibreBookId != delta.RecordId));
    }

    private static CalibreBook CopyBook(CalibreBook source, IEnumerable<BookFormat> formats) => new(
        source.Id, source.Title, source.AuthorSort, source.Authors, source.Identifiers, formats,
        source.RelativeDirectory, source.PublicationMetadata);

    private static ExactBinaryDuplicateGroup[] FilterBinaryGroups(
        IEnumerable<ExactBinaryDuplicateGroup> groups,
        Func<ExactBinaryDuplicateMember, bool> remove)
    {
        List<ExactBinaryDuplicateGroup> projected = [];
        foreach (ExactBinaryDuplicateGroup group in groups)
        {
            ExactBinaryDuplicateMember[] members = group.Members.Where(value => !remove(value)).ToArray();
            if (members.Length >= 2) projected.Add(new(group.Id, group.Fingerprint, members));
        }
        return projected.ToArray();
    }

    private static ExactMetadataDuplicateGroup? CreateMetadataGroup(
        ExactMetadataDuplicateGroup source,
        IEnumerable<CalibreBookId> members)
    {
        CalibreBookId[] values = members.ToArray();
        return values.Length < 2 ? null : new(source.Identity, values);
    }

    private static LibrarySnapshot Project(
        LibrarySnapshot source,
        IEnumerable<CalibreBook> books,
        IEnumerable<Findings.LibraryFinding> findings,
        IEnumerable<ExactBinaryDuplicateGroup> binaryGroups,
        IEnumerable<ExactMetadataDuplicateGroup> metadataGroups,
        IEnumerable<Assessments.EpubAssessment> epubAssessments,
        IEnumerable<Assessments.PdfAssessment> pdfAssessments) => new(
        source.Identity,
        source.ScannedAt,
        books,
        findings,
        binaryGroups,
        metadataGroups,
        epubAssessments,
        consolidationRecommendations: [],
        pdfAssessments);
}
