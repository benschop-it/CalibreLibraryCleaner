using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record QuarantineReviewSubject
{
    public QuarantineReviewSubject(
        CalibreBookId bookId,
        string currentTitle,
        IEnumerable<BookAuthor> currentAuthors,
        IEnumerable<FinalAuthorFormatIdentity> formats,
        FinalRecordDispositionAssessment disposition,
        bool manuallyIncluded = false,
        bool? include = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTitle);
        ArgumentNullException.ThrowIfNull(currentAuthors);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(disposition);
        if (!disposition.ProposedForQuarantine && !manuallyIncluded)
            throw new ArgumentException("A quarantine review subject requires policy or manual inclusion.");
        BookId = bookId;
        CurrentTitle = currentTitle;
        CurrentAuthors = new ReadOnlyCollection<BookAuthor>(currentAuthors.ToArray());
        Formats = new ReadOnlyCollection<FinalAuthorFormatIdentity>(formats
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray());
        Disposition = disposition;
        ManuallyIncluded = manuallyIncluded;
        Include = include ?? true;
    }

    public CalibreBookId BookId { get; }
    public string CurrentTitle { get; }
    public IReadOnlyList<BookAuthor> CurrentAuthors { get; }
    public IReadOnlyList<FinalAuthorFormatIdentity> Formats { get; }
    public FinalRecordDispositionAssessment Disposition { get; }
    public bool ManuallyIncluded { get; }
    public bool Include { get; }

    public QuarantineReviewSubject WithInclude(bool include) => new(
        BookId,
        CurrentTitle,
        CurrentAuthors,
        Formats,
        Disposition,
        ManuallyIncluded,
        include);
}

public sealed record QuarantineReviewWorkspace
{
    public QuarantineReviewWorkspace(
        string libraryRoot,
        string destinationRoot,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        IEnumerable<QuarantineReviewSubject> subjects,
        QuarantineDestinationIdentity? destinationIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(subjects);
        QuarantineReviewSubject[] values = subjects.OrderBy(value => value.BookId.Value).ToArray();
        if (values.Select(value => value.BookId).Distinct().Count() != values.Length)
            throw new ArgumentException("Quarantine review subjects must be unique.", nameof(subjects));
        LibraryRoot = libraryRoot;
        DestinationRoot = destinationRoot;
        GenerationId = generationId;
        Revision = revision;
        Subjects = new ReadOnlyCollection<QuarantineReviewSubject>(values);
        DestinationIdentity = destinationIdentity;
        DispositionPolicyVersion = FinalRecordDispositionPolicy.PolicyVersion;
    }

    public string LibraryRoot { get; }
    public string DestinationRoot { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
    public IReadOnlyList<QuarantineReviewSubject> Subjects { get; }
    public QuarantineDestinationIdentity? DestinationIdentity { get; }
    public string DispositionPolicyVersion { get; }
}

public sealed record QuarantineDestinationIdentity
{
    public QuarantineDestinationIdentity(bool exists, string? libraryUuid)
    {
        if (exists != !string.IsNullOrWhiteSpace(libraryUuid)
            || libraryUuid is not null && !Guid.TryParse(libraryUuid, out _))
            throw new ArgumentException("The quarantine destination identity is invalid.");
        Exists = exists;
        LibraryUuid = libraryUuid;
    }

    public bool Exists { get; }
    public string? LibraryUuid { get; }
}

public static class PrepareQuarantineReviewUseCase
{
    private const string DestinationSuffix = " - Unresolvable";

    public static QuarantineReviewWorkspace Execute(
        LibraryState state,
        FinalAuthorReviewWorkspace finalAuthorReview)
    {
        ValidateCurrent(state, finalAuthorReview);
        Dictionary<CalibreBookId, FinalAuthorReviewSubject> finalSubjects = finalAuthorReview.Subjects
            .ToDictionary(value => value.BookId);
        QuarantineReviewSubject[] subjects = state.Snapshot.Books
            .OrderBy(value => value.Id.Value)
            .Select(book => Assess(book, finalSubjects.GetValueOrDefault(book.Id)))
            .Where(value => value is not null)
            .Cast<QuarantineReviewSubject>()
            .ToArray();
        return new(
            state.Snapshot.Identity.LibraryRoot,
            DestinationRoot(state.Snapshot.Identity.LibraryRoot),
            state.GenerationId,
            state.Revision,
            subjects);
    }

    public static QuarantineReviewWorkspace SetInclude(
        QuarantineReviewWorkspace workspace,
        IReadOnlyCollection<CalibreBookId> bookIds,
        bool include)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(bookIds);
        HashSet<CalibreBookId> selected = bookIds.ToHashSet();
        if (selected.Count != bookIds.Count
            || selected.Any(id => workspace.Subjects.All(value => value.BookId != id)))
            throw new ArgumentException("Quarantine review selections are not current.", nameof(bookIds));
        return Copy(workspace, workspace.Subjects.Select(value =>
            selected.Contains(value.BookId) ? value.WithInclude(include) : value));
    }

    public static QuarantineReviewWorkspace SetAllInclude(
        QuarantineReviewWorkspace workspace,
        bool include) => SetInclude(
        workspace,
        workspace.Subjects.Select(value => value.BookId).ToArray(),
        include);

    public static QuarantineReviewWorkspace BindDestination(
        QuarantineReviewWorkspace workspace,
        QuarantineDestinationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(identity);
        return Copy(workspace, workspace.Subjects, identity);
    }

    public static QuarantineReviewWorkspace IncludeManually(
        LibraryState state,
        QuarantineReviewWorkspace workspace,
        CalibreBookId bookId)
    {
        ValidateCurrent(state, workspace);
        if (workspace.Subjects.Any(value => value.BookId == bookId))
            return SetInclude(workspace, [bookId], true);
        CalibreBook book = state.Snapshot.Books.SingleOrDefault(value => value.Id == bookId)
            ?? throw new ArgumentException("The manual quarantine record is not current.", nameof(bookId));
        FinalRecordDispositionAssessment disposition = FinalRecordDispositionPolicy.Assess(
            AuthorResolutionStatus.Known,
            [],
            inspectionExhausted: true,
            Labels(book));
        QuarantineReviewSubject manual = Subject(book, disposition, manuallyIncluded: true);
        return Copy(workspace, workspace.Subjects.Append(manual));
    }

    private static QuarantineReviewSubject? Assess(
        CalibreBook book,
        FinalAuthorReviewSubject? finalSubject)
    {
        AuthorResolutionStatus status = finalSubject?.Resolution.Status ?? AuthorResolutionStatus.Known;
        IReadOnlyList<string> reasons = finalSubject?.Resolution.ReasonCodes ?? [];
        bool inspectionExhausted = finalSubject is not null
            && !finalSubject.NeedsInspection
            && finalSubject.Resolution.ProposalSource == AuthorProposalSource.None;
        FinalRecordDispositionAssessment disposition = FinalRecordDispositionPolicy.Assess(
            status,
            reasons,
            inspectionExhausted,
            Labels(book));
        return disposition.ProposedForQuarantine ? Subject(book, disposition) : null;
    }

    private static QuarantineReviewSubject Subject(
        CalibreBook book,
        FinalRecordDispositionAssessment disposition,
        bool manuallyIncluded = false) => new(
        book.Id,
        book.Title,
        book.Authors,
        book.Formats.Select(value => new FinalAuthorFormatIdentity(
            value.Format,
            value.ExpectedRelativePath,
            value.FileStatus,
            value.Fingerprint)),
        disposition,
        manuallyIncluded);

    private static IEnumerable<FinalRecordDispositionLabel> Labels(CalibreBook book)
    {
        IEnumerable<(FinalRecordDispositionLabelSource Source, string Value)> candidates =
            new[] { (FinalRecordDispositionLabelSource.Title, book.Title) }
                .Concat(book.Authors.Select(value =>
                    (FinalRecordDispositionLabelSource.Author, value.Name)))
                .Concat(book.Identifiers.Select(value =>
                    (FinalRecordDispositionLabelSource.Identifier, value.Value)))
                .Concat(book.RelativeDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => (FinalRecordDispositionLabelSource.ManagedLayout, value)))
                .Concat(book.Formats.Select(value =>
                    (FinalRecordDispositionLabelSource.ManagedLayout,
                        Path.GetFileNameWithoutExtension(value.StoredFileName))));
        foreach ((FinalRecordDispositionLabelSource source, string value) in candidates.Take(100))
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > 512
                || value.Any(char.IsControl))
                continue;
            yield return new(source, value);
        }
    }

    private static string DestinationRoot(string libraryRoot)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        string? parent = Path.GetDirectoryName(source);
        string name = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("The source library cannot have a sibling quarantine library.");
        return Path.Combine(parent, name + DestinationSuffix);
    }

    private static void ValidateCurrent(
        LibraryState state,
        FinalAuthorReviewWorkspace finalAuthorReview)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(finalAuthorReview);
        if (!state.IsAuthoritative
            || !state.IsWorkflowCheckpointCurrent
            || state.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.Completed
            || finalAuthorReview.GenerationId != state.GenerationId
            || finalAuthorReview.Revision != state.Revision
            || !string.Equals(
                Path.GetFullPath(finalAuthorReview.LibraryRoot),
                Path.GetFullPath(state.Snapshot.Identity.LibraryRoot),
                StringComparison.OrdinalIgnoreCase)
            || finalAuthorReview.Subjects.Any(value => value.Apply))
            throw new InvalidOperationException(
                "Quarantine review requires current completed final-author review with no checked corrections.");
    }

    private static void ValidateCurrent(LibraryState state, QuarantineReviewWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!state.IsAuthoritative
            || !state.IsWorkflowCheckpointCurrent
            || state.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.Completed
            || workspace.GenerationId != state.GenerationId
            || workspace.Revision != state.Revision
            || workspace.DispositionPolicyVersion != FinalRecordDispositionPolicy.PolicyVersion
            || !string.Equals(
                Path.GetFullPath(workspace.LibraryRoot),
                Path.GetFullPath(state.Snapshot.Identity.LibraryRoot),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFullPath(workspace.DestinationRoot),
                DestinationRoot(state.Snapshot.Identity.LibraryRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Quarantine review workspace is stale.");
    }

    private static QuarantineReviewWorkspace Copy(
        QuarantineReviewWorkspace source,
        IEnumerable<QuarantineReviewSubject> subjects,
        QuarantineDestinationIdentity? destinationIdentity = null) => new(
        source.LibraryRoot,
        source.DestinationRoot,
        source.GenerationId,
        source.Revision,
        subjects,
        destinationIdentity ?? source.DestinationIdentity);
}
