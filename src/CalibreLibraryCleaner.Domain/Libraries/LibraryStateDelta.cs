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

public sealed record AddOrReplaceFormatLibraryStateDelta : LibraryStateDelta
{
    public AddOrReplaceFormatLibraryStateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        string operationId,
        DateTimeOffset appliedAtUtc,
        CalibreBookId recordId,
        string format,
        FormatFileFingerprint fingerprint,
        FormatFileFingerprint? expectedPreviousFingerprint)
        : base(generationId, expectedRevision, operationId, appliedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        RecordId = recordId;
        Format = format.Trim().ToUpperInvariant();
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        ExpectedPreviousFingerprint = expectedPreviousFingerprint;
    }

    public CalibreBookId RecordId { get; }
    public string Format { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public FormatFileFingerprint? ExpectedPreviousFingerprint { get; }
}

public sealed record RemoveRecordWithContentLibraryStateDelta : LibraryStateDelta
{
    public RemoveRecordWithContentLibraryStateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        string operationId,
        DateTimeOffset appliedAtUtc,
        CalibreBookId recordId)
        : base(generationId, expectedRevision, operationId, appliedAtUtc) => RecordId = recordId;

    public CalibreBookId RecordId { get; }
}

public sealed record CreateRecordLibraryStateDelta : LibraryStateDelta
{
    public CreateRecordLibraryStateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        string operationId,
        DateTimeOffset appliedAtUtc,
        CalibreBookId recordId,
        string title,
        IEnumerable<string> authors,
        string authorSort)
        : base(generationId, expectedRevision, operationId, appliedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(authors);
        ArgumentNullException.ThrowIfNull(authorSort);
        string[] values = authors.Select(value =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            return value;
        }).ToArray();
        if (values.Length == 0) throw new ArgumentException("A projected record requires at least one author.", nameof(authors));
        RecordId = recordId;
        Title = title;
        Authors = Array.AsReadOnly(values);
        AuthorSort = authorSort;
    }

    public CalibreBookId RecordId { get; }
    public string Title { get; }
    public IReadOnlyList<string> Authors { get; }
    public string AuthorSort { get; }
}

public enum LibraryMetadataField
{
    Title,
    Authors,
    AuthorSort,
    Publisher,
    PublicationDate,
    Languages,
    Identifiers,
    Series,
    SeriesIndex,
}

public sealed record SetMetadataLibraryStateDelta : LibraryStateDelta
{
    public SetMetadataLibraryStateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        string operationId,
        DateTimeOffset appliedAtUtc,
        CalibreBookId recordId,
        LibraryMetadataField field,
        IEnumerable<string> values)
        : base(generationId, expectedRevision, operationId, appliedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(field)) throw new ArgumentOutOfRangeException(nameof(field));
        RecordId = recordId;
        Field = field;
        Values = Array.AsReadOnly(values.Select(value => value ?? throw new ArgumentException(
            "Metadata values cannot contain null.", nameof(values))).ToArray());
    }

    public CalibreBookId RecordId { get; }
    public LibraryMetadataField Field { get; }
    public IReadOnlyList<string> Values { get; }
}

public static class LibraryStateDeltaPolicy
{
    public static LibraryState Apply(LibraryState state, LibraryStateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(delta);
        ValidateTransition(state, delta, state.Revision, state.ProjectedAtUtc);

        LibrarySnapshot projected = delta switch
        {
            RemoveFormatLibraryStateDelta removeFormat => RemoveFormat(state.Snapshot, removeFormat),
            RemoveRecordLibraryStateDelta removeRecord => RemoveRecord(state.Snapshot, removeRecord),
            AddOrReplaceFormatLibraryStateDelta addOrReplace => AddOrReplaceFormat(state.Snapshot, addOrReplace),
            RemoveRecordWithContentLibraryStateDelta removeWithContent => RemoveRecord(
                state.Snapshot, removeWithContent.RecordId, requireEmpty: false),
            CreateRecordLibraryStateDelta createRecord => CreateRecord(state.Snapshot, createRecord),
            SetMetadataLibraryStateDelta setMetadata => SetMetadata(state.Snapshot, setMetadata),
            _ => throw new ArgumentOutOfRangeException(nameof(delta), delta.GetType().Name, "Unsupported library-state delta."),
        };
        return new(state.GenerationId, state.Revision.Next(), LibraryStateStatus.Authoritative,
            projected, delta.AppliedAtUtc, workflowCheckpoint: state.WorkflowCheckpoint);
    }

    public static LibraryState ApplyBatch(
        LibraryState state,
        IReadOnlyList<LibraryStateDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(deltas);
        if (deltas.Count == 0) throw new ArgumentException("At least one state delta is required.", nameof(deltas));
        if (deltas.Any(delta => delta is not RemoveFormatLibraryStateDelta
            and not RemoveRecordLibraryStateDelta
            and not AddOrReplaceFormatLibraryStateDelta))
        {
            LibraryState sequential = state;
            foreach (LibraryStateDelta delta in deltas) sequential = Apply(sequential, delta);
            return sequential;
        }

        Dictionary<CalibreBookId, CalibreBook> books = state.Snapshot.Books.ToDictionary(value => value.Id);
        HashSet<FormatAssociation> changedFormats = [];
        HashSet<CalibreBookId> removedRecords = [];
        LibraryStateRevision revision = state.Revision;
        DateTimeOffset projectedAt = state.ProjectedAtUtc;
        foreach (LibraryStateDelta delta in deltas)
        {
            ArgumentNullException.ThrowIfNull(delta);
            ValidateTransition(state, delta, revision, projectedAt);
            switch (delta)
            {
                case RemoveFormatLibraryStateDelta removeFormat:
                    ApplyRemoveFormat(books, changedFormats, removeFormat);
                    break;
                case AddOrReplaceFormatLibraryStateDelta addOrReplace:
                    ApplyAddOrReplaceFormat(books, changedFormats, addOrReplace);
                    break;
                case RemoveRecordLibraryStateDelta removeRecord:
                    ApplyRemoveRecord(books, removedRecords, removeRecord);
                    break;
            }
            revision = revision.Next();
            projectedAt = delta.AppliedAtUtc;
        }

        CalibreBook[] projectedBooks = state.Snapshot.Books
            .Where(value => books.ContainsKey(value.Id))
            .Select(value => books[value.Id])
            .ToArray();
        ExactMetadataDuplicateGroup[] metadataGroups = state.Snapshot.ExactMetadataDuplicateGroups
            .Select(group => group.Members.Any(removedRecords.Contains)
                ? CreateMetadataGroup(group, group.Members.Where(value => !removedRecords.Contains(value)))
                : group)
            .Where(value => value is not null)
            .Cast<ExactMetadataDuplicateGroup>()
            .ToArray();
        LibrarySnapshot snapshot = Project(state.Snapshot, projectedBooks,
            state.Snapshot.Findings.Where(value => value.BookId is null
                || !removedRecords.Contains(value.BookId.Value)
                && (value.Format is null
                    || !changedFormats.Contains(new(value.BookId.Value, value.Format)))),
            ExactBinaryDuplicateDetector.Detect(projectedBooks),
            metadataGroups,
            state.Snapshot.EpubAssessments.Where(value => !removedRecords.Contains(value.CalibreBookId)
                && !changedFormats.Contains(new(value.CalibreBookId, value.Format))),
            state.Snapshot.PdfAssessments.Where(value => !removedRecords.Contains(value.CalibreBookId)
                && !changedFormats.Contains(new(value.CalibreBookId, value.Format))));
        return new(state.GenerationId, revision, LibraryStateStatus.Authoritative, snapshot, projectedAt,
            workflowCheckpoint: state.WorkflowCheckpoint);
    }

    private static void ValidateTransition(
        LibraryState state,
        LibraryStateDelta delta,
        LibraryStateRevision expectedRevision,
        DateTimeOffset projectedAt)
    {
        if (!state.IsAuthoritative)
            throw new InvalidOperationException("Deltas cannot be applied while library state is uncertain.");
        if (delta.GenerationId != state.GenerationId)
            throw new InvalidOperationException("The delta belongs to a different library-state generation.");
        if (delta.ExpectedRevision != expectedRevision)
            throw new InvalidOperationException("The delta expected a different library-state revision.");
        if (delta.AppliedAtUtc < projectedAt)
            throw new InvalidOperationException("The delta predates the current projected state.");
    }

    private static void ApplyRemoveFormat(
        Dictionary<CalibreBookId, CalibreBook> books,
        HashSet<FormatAssociation> changedFormats,
        RemoveFormatLibraryStateDelta delta)
    {
        if (!books.TryGetValue(delta.RecordId, out CalibreBook? current))
            throw new InvalidOperationException("The format-removal record is not present in the authoritative state.");
        BookFormat removed = current.Formats.SingleOrDefault(value =>
            string.Equals(value.Format, delta.Format, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The format-removal association is not present in the authoritative state.");
        if (removed.Fingerprint != delta.ExpectedFingerprint)
            throw new InvalidOperationException("The format-removal fingerprint does not match authoritative state.");
        books[delta.RecordId] = CopyBook(current, current.Formats.Where(value => value != removed));
        changedFormats.Add(new(delta.RecordId, delta.Format));
    }

    private static void ApplyAddOrReplaceFormat(
        Dictionary<CalibreBookId, CalibreBook> books,
        HashSet<FormatAssociation> changedFormats,
        AddOrReplaceFormatLibraryStateDelta delta)
    {
        if (!books.TryGetValue(delta.RecordId, out CalibreBook? current))
            throw new InvalidOperationException("The format target record is not present in authoritative state.");
        BookFormat? existing = current.Formats.SingleOrDefault(value =>
            string.Equals(value.Format, delta.Format, StringComparison.Ordinal));
        if (existing?.Fingerprint != delta.ExpectedPreviousFingerprint)
            throw new InvalidOperationException("The target format does not match the expected previous fingerprint.");
        BookFormat projectedFormat = new(delta.Format, string.Empty, string.Empty,
            FormatFileStatus.ProjectedPresent, delta.Fingerprint);
        BookFormat[] formats = current.Formats
            .Where(value => !string.Equals(value.Format, delta.Format, StringComparison.Ordinal))
            .Append(projectedFormat)
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ToArray();
        books[delta.RecordId] = CopyBook(current, formats);
        changedFormats.Add(new(delta.RecordId, delta.Format));
    }

    private static void ApplyRemoveRecord(
        Dictionary<CalibreBookId, CalibreBook> books,
        HashSet<CalibreBookId> removedRecords,
        RemoveRecordLibraryStateDelta delta)
    {
        if (!books.TryGetValue(delta.RecordId, out CalibreBook? removed))
            throw new InvalidOperationException("The record removal target is not present in the authoritative state.");
        if (removed.Formats.Count != 0)
            throw new InvalidOperationException("A record can be projected as removed only after all formats are absent.");
        books.Remove(delta.RecordId);
        removedRecords.Add(delta.RecordId);
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
        RemoveRecordLibraryStateDelta delta) => RemoveRecord(snapshot, delta.RecordId, requireEmpty: true);

    private static LibrarySnapshot RemoveRecord(
        LibrarySnapshot snapshot,
        CalibreBookId recordId,
        bool requireEmpty)
    {
        CalibreBook removed = snapshot.Books.SingleOrDefault(value => value.Id == recordId)
            ?? throw new InvalidOperationException("The record removal target is not present in the authoritative state.");
        if (requireEmpty && removed.Formats.Count != 0)
            throw new InvalidOperationException("A record can be projected as removed only after all formats are absent.");

        CalibreBook[] books = snapshot.Books.Where(value => value.Id != recordId).ToArray();
        ExactBinaryDuplicateGroup[] binaryGroups = FilterBinaryGroups(snapshot.ExactBinaryDuplicateGroups,
            member => member.BookId == recordId);
        ExactMetadataDuplicateGroup[] metadataGroups = snapshot.ExactMetadataDuplicateGroups
            .Select(group => group.Members.Contains(recordId)
                ? CreateMetadataGroup(group, group.Members.Where(value => value != recordId))
                : group)
            .Where(value => value is not null)
            .Cast<ExactMetadataDuplicateGroup>()
            .ToArray();
        return Project(snapshot, books,
            snapshot.Findings.Where(value => value.BookId != recordId),
            binaryGroups,
            metadataGroups,
            snapshot.EpubAssessments.Where(value => value.CalibreBookId != recordId),
            snapshot.PdfAssessments.Where(value => value.CalibreBookId != recordId));
    }

    private static LibrarySnapshot AddOrReplaceFormat(
        LibrarySnapshot snapshot,
        AddOrReplaceFormatLibraryStateDelta delta)
    {
        CalibreBook current = snapshot.Books.SingleOrDefault(value => value.Id == delta.RecordId)
            ?? throw new InvalidOperationException("The format target record is not present in authoritative state.");
        BookFormat? existing = current.Formats.SingleOrDefault(value =>
            string.Equals(value.Format, delta.Format, StringComparison.Ordinal));
        if (existing?.Fingerprint != delta.ExpectedPreviousFingerprint)
            throw new InvalidOperationException("The target format does not match the expected previous fingerprint.");
        BookFormat projectedFormat = new(delta.Format, string.Empty, string.Empty,
            FormatFileStatus.ProjectedPresent, delta.Fingerprint);
        BookFormat[] formats = current.Formats
            .Where(value => !string.Equals(value.Format, delta.Format, StringComparison.Ordinal))
            .Append(projectedFormat)
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ToArray();
        CalibreBook replacement = CopyBook(current, formats);
        CalibreBook[] books = snapshot.Books.Select(value => value.Id == current.Id ? replacement : value).ToArray();
        ExactBinaryDuplicateGroup[] binaryGroups = ExactBinaryDuplicateDetector.Detect(books).ToArray();
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

    private static LibrarySnapshot CreateRecord(
        LibrarySnapshot snapshot,
        CreateRecordLibraryStateDelta delta)
    {
        if (snapshot.Books.Any(value => value.Id == delta.RecordId))
            throw new InvalidOperationException("The projected created record ID already exists.");
        CalibreBook created = new(delta.RecordId, delta.Title, delta.AuthorSort,
            delta.Authors.Select(value => new BookAuthor(null, value, value)), [], [], string.Empty);
        CalibreBook[] books = snapshot.Books.Append(created).OrderBy(value => value.Id.Value).ToArray();
        ExactMetadataDuplicateGroup[] metadataGroups = ExactMetadataDuplicateDetector.Detect(books).ToArray();
        return Project(snapshot, books, snapshot.Findings, snapshot.ExactBinaryDuplicateGroups,
            metadataGroups, snapshot.EpubAssessments, snapshot.PdfAssessments);
    }

    private static LibrarySnapshot SetMetadata(
        LibrarySnapshot snapshot,
        SetMetadataLibraryStateDelta delta)
    {
        CalibreBook current = snapshot.Books.SingleOrDefault(value => value.Id == delta.RecordId)
            ?? throw new InvalidOperationException("The metadata target record is not present in authoritative state.");
        CalibreBook replacement = UpdateMetadata(current, delta);
        CalibreBook[] books = snapshot.Books.Select(value => value.Id == current.Id ? replacement : value).ToArray();
        ExactMetadataDuplicateGroup[] metadataGroups = ExactMetadataDuplicateDetector.Detect(books).ToArray();
        return Project(snapshot, books, snapshot.Findings, snapshot.ExactBinaryDuplicateGroups,
            metadataGroups, snapshot.EpubAssessments, snapshot.PdfAssessments);
    }

    private static CalibreBook UpdateMetadata(CalibreBook source, SetMetadataLibraryStateDelta delta)
    {
        string title = source.Title;
        string authorSort = source.AuthorSort;
        IReadOnlyList<BookAuthor> authors = source.Authors;
        IReadOnlyList<BookIdentifier> identifiers = source.Identifiers;
        BookPublicationMetadata publication = source.PublicationMetadata;
        switch (delta.Field)
        {
            case LibraryMetadataField.Title:
                title = Single(delta);
                break;
            case LibraryMetadataField.Authors:
                if (delta.Values.Count == 0) throw new InvalidOperationException("Authors cannot be empty.");
                authors = delta.Values.Select(value => new BookAuthor(null, value, value)).ToArray();
                break;
            case LibraryMetadataField.AuthorSort:
                authorSort = Single(delta);
                break;
            case LibraryMetadataField.Identifiers:
                identifiers = delta.Values.Select(ParseIdentifier).ToArray();
                break;
            default:
                publication = UpdatePublication(publication, delta);
                break;
        }
        return new(source.Id, title, authorSort, authors, identifiers, source.Formats,
            source.RelativeDirectory, publication);
    }

    private static BookPublicationMetadata UpdatePublication(
        BookPublicationMetadata source,
        SetMetadataLibraryStateDelta delta)
    {
        string? publisher = source.Publisher;
        DateTimeOffset? publicationDate = source.PublicationDate;
        string? series = source.Series;
        decimal? seriesIndex = source.SeriesIndex;
        IReadOnlyList<string> languages = source.Languages;
        switch (delta.Field)
        {
            case LibraryMetadataField.Publisher:
                publisher = OptionalSingle(delta);
                break;
            case LibraryMetadataField.PublicationDate:
                string? date = OptionalSingle(delta);
                publicationDate = date is null ? null : DateTimeOffset.Parse(date,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                break;
            case LibraryMetadataField.Languages:
                languages = delta.Values;
                break;
            case LibraryMetadataField.Series:
                series = OptionalSingle(delta);
                break;
            case LibraryMetadataField.SeriesIndex:
                string? index = OptionalSingle(delta);
                seriesIndex = index is null ? null : decimal.Parse(index,
                    System.Globalization.CultureInfo.InvariantCulture);
                break;
            default:
                throw new InvalidOperationException("The metadata field is not a publication field.");
        }
        return new(publisher, publicationDate, series, seriesIndex, languages, source.HasCover);
    }

    private static string Single(SetMetadataLibraryStateDelta delta) => delta.Values.Count == 1
        && !string.IsNullOrWhiteSpace(delta.Values[0])
        ? delta.Values[0]
        : throw new InvalidOperationException("The metadata field requires one nonblank value.");

    private static string? OptionalSingle(SetMetadataLibraryStateDelta delta)
    {
        if (delta.Values.Count != 1) throw new InvalidOperationException("The metadata field requires one value.");
        return string.IsNullOrWhiteSpace(delta.Values[0]) ? null : delta.Values[0];
    }

    private static BookIdentifier ParseIdentifier(string value)
    {
        int separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidOperationException("A projected identifier must use type:value syntax.");
        return new(value[..separator], value[(separator + 1)..]);
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

    private readonly record struct FormatAssociation(CalibreBookId RecordId, string Format);
}
