using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
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
        bool? include = null,
        IEnumerable<FrontMatterIdentityCandidate>? identityCandidates = null,
        FinalRecordInputIdentity? inputIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTitle);
        ArgumentNullException.ThrowIfNull(currentAuthors);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(disposition);
        BookId = bookId;
        CurrentTitle = currentTitle;
        CurrentAuthors = new ReadOnlyCollection<BookAuthor>(currentAuthors.ToArray());
        Formats = new ReadOnlyCollection<FinalAuthorFormatIdentity>(formats
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray());
        Disposition = disposition;
        IdentityCandidates = Array.AsReadOnly((identityCandidates ?? []).Take(16).ToArray());
        bool swapped = CurrentAuthors.Count == 1 && IdentityCandidates.Any(value =>
            value.ExplicitCredit && value.Authors.Count == 1
            && SameTitle(value.CompleteTitle, CurrentAuthors[0].Name)
            && SameTitle(value.Authors[0], CurrentTitle));
        MetadataProblemReasonCodes = Array.AsReadOnly(RecordMetadataProblemPolicy.Assess(
            currentTitle, CurrentAuthors.Select(value => value.Name))
            .Concat(swapped ? [RecordMetadataProblemPolicy.TitleAuthorSwapped] : Array.Empty<string>()).ToArray());
        InputIdentity = inputIdentity;
        ManuallyIncluded = manuallyIncluded;
        Include = include ?? (disposition.ProposedForQuarantine || manuallyIncluded);
        if (Include && !disposition.ProposedForQuarantine && !manuallyIncluded)
            throw new ArgumentException("A pending record requires explicit manual transfer assignment.");
    }

    public CalibreBookId BookId { get; }
    public string CurrentTitle { get; }
    public IReadOnlyList<BookAuthor> CurrentAuthors { get; }
    public IReadOnlyList<string> MetadataProblemReasonCodes { get; }
    public IReadOnlyList<FinalAuthorFormatIdentity> Formats { get; }
    public FinalRecordDispositionAssessment Disposition { get; }
    public bool ManuallyIncluded { get; }
    public bool Include { get; }
    public bool CanAcceptReviewed => Disposition.Evidence?.ManagedLayoutVerified == true
        && Formats.Any(value => value.FileStatus == FormatFileStatus.Present && value.Fingerprint is not null);
    public FinalRecordQualification Qualification => Include && ManuallyIncluded
        ? FinalRecordQualification.ProposedUnresolvable : Disposition.Qualification;
    public IReadOnlyList<FrontMatterIdentityCandidate> IdentityCandidates { get; }
    public FinalRecordInputIdentity? InputIdentity { get; }

    public QuarantineReviewSubject WithInclude(bool include) => new(
        BookId,
        CurrentTitle,
        CurrentAuthors,
        Formats,
        Disposition,
        ManuallyIncluded || include && !Disposition.ProposedForQuarantine,
        include,
        IdentityCandidates,
        InputIdentity);

    private static bool SameTitle(string left, string right) =>
        MetadataTextNormalizer.TryNormalizeTitle(left, out NormalizedTitle? a)
        && MetadataTextNormalizer.TryNormalizeTitle(right, out NormalizedTitle? b) && a == b;

    public QuarantineReviewSubject AcceptReviewed()
    {
        FinalRecordQualificationEvidence evidence = Disposition.Evidence
            ?? throw new InvalidOperationException("Current qualification evidence is required.");
        if (!evidence.ManagedLayoutVerified)
            throw new InvalidOperationException("Managed naming must be verified before reviewer acceptance.");
        if (!CanAcceptReviewed)
            throw new InvalidOperationException("Reviewer acceptance requires retained content with verified file identity.");
        FinalRecordQualificationEvidence accepted = new(
            evidence.TitleSupported, evidence.AuthorsSupported, evidence.Content,
            evidence.ManagedLayoutVerified, evidence.MaterialIdentityProblem,
            concreteRepairAvailable: false, evidence.Conflicting, evidence.Routes, reviewerAccepted: true);
        return new(
            BookId, CurrentTitle, CurrentAuthors, Formats,
            new(["RECORD.REVIEWER_ACCEPTED"], [], FinalRecordQualificationPolicy.Assess(accepted), accepted),
            include: false, identityCandidates: IdentityCandidates, inputIdentity: InputIdentity);
    }
}

public sealed record QuarantineReviewWorkspace
{
    public QuarantineReviewWorkspace(
        string libraryRoot,
        string destinationRoot,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        IEnumerable<QuarantineReviewSubject> subjects,
        QuarantineDestinationIdentity? destinationIdentity = null,
        int? retainedInputCount = null,
        int verifiedMovedCount = 0)
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
        RetainedInputCount = retainedInputCount ?? values.Length;
        VerifiedMovedCount = verifiedMovedCount;
        if (verifiedMovedCount < 0 || RetainedInputCount != values.Length + verifiedMovedCount)
            throw new ArgumentException("Final record accounting is inconsistent.");
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
    public int RetainedInputCount { get; }
    public int VerifiedMovedCount { get; }
    public int QualifiedCount => Subjects.Count(value =>
        !value.Include && value.Disposition.Qualification == FinalRecordQualification.Qualified);
    public int RepairableCount => Subjects.Count(value =>
        value.Disposition.Qualification == FinalRecordQualification.Repairable);
    public int PendingCount => Subjects.Count - QualifiedCount;
    public bool IsHighQualityComplete => PendingCount == 0
        && RetainedInputCount == QualifiedCount + VerifiedMovedCount;
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
        FinalAuthorReviewWorkspace finalAuthorReview,
        bool managedLayoutVerified = false,
        EditionMetadataProvidersBatchResult? providerResults = null,
        IReadOnlySet<CalibreBookId>? verifiedManagedNameBookIds = null)
    {
        ValidateCurrent(state, finalAuthorReview);
        if (providerResults is not null && providerResults.InputIdentities is null)
            throw new InvalidOperationException("Identity repair results have no current input binding.");
        Dictionary<CalibreBookId, FinalAuthorReviewSubject> finalSubjects = finalAuthorReview.Subjects
            .ToDictionary(value => value.BookId);
        ILookup<CalibreBookId, EpubAssessment> epubs = state.Snapshot.EpubAssessments.ToLookup(value => value.CalibreBookId);
        ILookup<CalibreBookId, PdfAssessment> pdfs = state.Snapshot.PdfAssessments.ToLookup(value => value.CalibreBookId);
        ILookup<FormatFileFingerprint, FrontMatterAuthorObservation> observations = finalAuthorReview
            .FrontMatterObservations.DistinctBy(value => new { value.Format, value.Fingerprint })
            .ToLookup(value => value.Fingerprint);
        HashSet<CalibreBookId> layoutProblems = state.Snapshot.Findings
            .Where(value => value.BookId is not null
                && value.Code is "MANAGED_PATH_INVALID" or "MANAGED_LAYOUT_UNSAFE"
                    or "MANAGED_LAYOUT_INTERNAL_STORAGE" or "MANAGED_LAYOUT_UNREGISTERED_FILES")
            .Select(value => value.BookId!.Value).ToHashSet();
        QuarantineReviewSubject[] subjects = state.Snapshot.Books
            .OrderBy(value => value.Id.Value)
            .Select(book => Assess(
                book, finalSubjects.GetValueOrDefault(book.Id),
                epubs[book.Id].ToArray(), pdfs[book.Id].ToArray(), observations,
                managedLayoutVerified && !layoutProblems.Contains(book.Id)
                    && (verifiedManagedNameBookIds is null || verifiedManagedNameBookIds.Contains(book.Id))
                    && book.Formats.Count > 0
                    && book.Formats.All(value => value.FileStatus == FormatFileStatus.Present
                        && value.Fingerprint is not null), providerResults))
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
        HashSet<CalibreBookId> available = workspace.Subjects.Select(value => value.BookId).ToHashSet();
        if (selected.Count != bookIds.Count
            || !selected.IsSubsetOf(available))
            throw new ArgumentException("Quarantine review selections are not current.", nameof(bookIds));
        return Copy(workspace, workspace.Subjects.Select(value =>
            selected.Contains(value.BookId) ? value.WithInclude(include) : value));
    }

    public static QuarantineReviewWorkspace ReassessSelected(
        LibraryState state,
        QuarantineReviewWorkspace workspace,
        FinalAuthorReviewWorkspace finalAuthorReview,
        EditionMetadataProvidersBatchResult providerResults,
        IReadOnlyCollection<CalibreBookId> bookIds,
        IReadOnlySet<CalibreBookId> verifiedManagedNameBookIds)
    {
        ValidateCurrent(state, workspace);
        ArgumentNullException.ThrowIfNull(bookIds);
        HashSet<CalibreBookId> selected = bookIds.ToHashSet();
        if (selected.Count == 0 || selected.Count != bookIds.Count
            || !selected.IsSubsetOf(workspace.Subjects.Select(value => value.BookId))
            || providerResults.InputIdentities is null
            || !selected.SetEquals(providerResults.InputIdentities.Keys))
            throw new ArgumentException("Selected repair evidence does not match the current review records.", nameof(bookIds));
        QuarantineReviewWorkspace assessed = Execute(
            state, finalAuthorReview, true, providerResults, verifiedManagedNameBookIds);
        Dictionary<CalibreBookId, QuarantineReviewSubject> replacements = assessed.Subjects
            .Where(value => selected.Contains(value.BookId)).ToDictionary(value => value.BookId);
        return Copy(workspace, workspace.Subjects.Select(previous =>
        {
            if (!selected.Contains(previous.BookId)) return previous;
            QuarantineReviewSubject current = replacements[previous.BookId];
            return new QuarantineReviewSubject(
                current.BookId, current.CurrentTitle, current.CurrentAuthors, current.Formats, current.Disposition,
                previous.ManuallyIncluded || previous.Include && !current.Disposition.ProposedForQuarantine,
                previous.Include, current.IdentityCandidates, current.InputIdentity);
        }));
    }

    public static QuarantineReviewWorkspace SetAllInclude(
        QuarantineReviewWorkspace workspace,
        bool include) => SetInclude(
        workspace,
        workspace.Subjects.Where(value => !include || value.Disposition.ProposedForQuarantine || value.ManuallyIncluded)
            .Select(value => value.BookId).ToArray(),
        include);

    public static QuarantineReviewWorkspace AcceptReviewed(
        LibraryState state,
        QuarantineReviewWorkspace workspace,
        IReadOnlyCollection<CalibreBookId> bookIds)
    {
        ValidateCurrent(state, workspace);
        HashSet<CalibreBookId> selected = bookIds.ToHashSet();
        HashSet<CalibreBookId> available = workspace.Subjects.Select(value => value.BookId).ToHashSet();
        if (selected.Count != bookIds.Count || !selected.IsSubsetOf(available))
            throw new ArgumentException("Reviewer acceptance selections are stale.", nameof(bookIds));
        Dictionary<CalibreBookId, CalibreBook> books = state.Snapshot.Books.ToDictionary(value => value.Id);
        if (workspace.Subjects.Where(value => selected.Contains(value.BookId))
            .Any(value => !books.TryGetValue(value.BookId, out CalibreBook? book)
                || value.InputIdentity != FinalRecordInputIdentity.Create(book)))
            throw new InvalidOperationException("Reviewer acceptance evidence changed; prepare a new assessment.");
        return Copy(workspace, workspace.Subjects.Select(value =>
            selected.Contains(value.BookId) ? value.AcceptReviewed() : value));
    }

    public static QuarantineReviewWorkspace RecordVerifiedMoves(
        QuarantineReviewWorkspace workspace,
        IReadOnlyCollection<CalibreBookId> movedIds)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(movedIds);
        HashSet<CalibreBookId> moved = movedIds.ToHashSet();
        if (moved.Count != movedIds.Count
            || !moved.SetEquals(workspace.Subjects.Where(value => value.Include).Select(value => value.BookId)))
            throw new InvalidOperationException("Verified move accounting does not match the reviewed selection.");
        return new(
            workspace.LibraryRoot, workspace.DestinationRoot, workspace.GenerationId, workspace.Revision,
            workspace.Subjects.Where(value => !moved.Contains(value.BookId)), workspace.DestinationIdentity,
            workspace.RetainedInputCount, checked(workspace.VerifiedMovedCount + moved.Count));
    }

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

    private static QuarantineReviewSubject Assess(
        CalibreBook book,
        FinalAuthorReviewSubject? finalSubject,
        IReadOnlyList<EpubAssessment> epubs,
        IReadOnlyList<PdfAssessment> pdfs,
        ILookup<FormatFileFingerprint, FrontMatterAuthorObservation> observations,
        bool managedLayoutVerified,
        EditionMetadataProvidersBatchResult? providers)
    {
        if (providers is not null)
        {
            if (!providers.InputIdentities!.TryGetValue(book.Id, out FinalRecordInputIdentity? expected))
                providers = null;
            else if (expected != FinalRecordInputIdentity.Create(book))
                throw new InvalidOperationException("Identity repair inputs changed; prepare a new repair review.");
        }
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
        RecordIdentityTriageAssessment triage = RecordIdentityTriagePolicy.Assess(
            book.Title, book.Authors.Select(value => value.Name));
        FrontMatterAuthorObservation[] local = book.Formats
            .Where(value => value.Fingerprint is not null)
            .SelectMany(format => observations[format.Fingerprint!].Where(value =>
                value.Format == format.Format && value.ExtractorVersion == (format.Format == "EPUB"
                    ? FrontMatterAuthorVersions.EpubExtractor : FrontMatterAuthorVersions.PdfExtractor)
                && value.LimitsVersion == FrontMatterAuthorVersions.Limits)).ToArray();
        FrontMatterIdentityCandidate[] pairs = local
            .Where(value => value.Coverage == FinalRecordRouteOutcome.CompletedWithEvidence)
            .SelectMany(value => value.IdentityCandidates).ToArray();
        bool agrees = pairs.Any(value => value.ExplicitCredit && IdentityTitleMatches(value, book.Title)
            && AuthorsMatch(value.Authors, book.Authors.Select(author => author.Name)));
        bool conflict = pairs.Any(value => value.ExplicitCredit
            && (!IdentityTitleMatches(value, book.Title)
                || !AuthorsMatch(value.Authors, book.Authors.Select(author => author.Name))));
        List<FinalRecordResolutionRoute> routes = [];
        foreach (BookFormat format in book.Formats.Where(value => value.Format is "EPUB" or "PDF"))
        {
            FrontMatterAuthorObservation? observation = local.FirstOrDefault(value =>
                value.Format == format.Format && value.Fingerprint == format.Fingerprint);
            routes.Add(new(
                $"local-{format.Format}", format.Format == "EPUB"
                    ? FrontMatterAuthorVersions.EpubExtractor : FrontMatterAuthorVersions.PdfExtractor,
                observation?.Coverage ?? (format.FileStatus == FormatFileStatus.Present && format.Fingerprint is not null
                    ? FinalRecordRouteOutcome.NotAttempted : FinalRecordRouteOutcome.Unavailable)));
        }
        if (book.Formats.Any(value => value.Format is not ("EPUB" or "PDF")))
            routes.Add(new("local-unsupported-formats", FrontMatterAuthorVersions.Limits,
                FinalRecordRouteOutcome.Unavailable));
        if (routes.Count == 0)
            routes.Add(new("local-identity", FrontMatterAuthorVersions.Limits, FinalRecordRouteOutcome.Unavailable));
        foreach (EditionMetadataProposalBatchResult provider in (providers?.Providers ?? [])
                     .Where(value => value.Provider.Id != "local-front-matter"))
        {
            EditionMetadataProposal? proposal = provider.Proposals.GetValueOrDefault(book.Id);
            routes.Add(new(
                provider.Provider.Id, provider.Provider.Version,
                provider.RequestLimitReached && proposal?.Status is null or EditionMetadataProposalStatus.NotFound
                    ? FinalRecordRouteOutcome.DeferredByBounds : proposal?.Status switch
                    {
                        EditionMetadataProposalStatus.NotFound => FinalRecordRouteOutcome.CompletedWithoutEvidence,
                        EditionMetadataProposalStatus.Proposed => proposal?.Candidate is { AuthorOrderKnown: false } candidate
                            && !EditionMetadataAuthorWritePolicy.TryPrepare(book, candidate, out _)
                                ? FinalRecordRouteOutcome.Unavailable : FinalRecordRouteOutcome.CompletedWithEvidence,
                        EditionMetadataProposalStatus.Ambiguous => FinalRecordRouteOutcome.Conflicting,
                        EditionMetadataProposalStatus.Unavailable => FinalRecordRouteOutcome.Unavailable,
                        _ => provider.RequestLimitReached ? FinalRecordRouteOutcome.DeferredByBounds
                            : FinalRecordRouteOutcome.Unavailable,
                    }));
        }
        if (providers is null || providers.Providers.All(value => value.Provider.Id == "local-front-matter"))
            routes.Add(new("bibliographic-repair", EditionMetadataLookupQuery.PolicyVersion,
                FinalRecordRouteOutcome.NotAttempted));
        EpubAssessment[] compatibleEpubs = epubs.Where(value => book.Formats.Any(format =>
            format.Format == "EPUB" && format.FileStatus == FormatFileStatus.Present
            && format.Fingerprint == value.ObservedFingerprint)
            && value.AnalyzerVersion == EpubAssessmentEngine.AnalyzerVersion
            && value.ScoringModelVersion == EpubAssessmentEngine.ScoringModelVersion).ToArray();
        PdfAssessment[] compatiblePdfs = pdfs.Where(value => book.Formats.Any(format =>
            format.Format == "PDF" && format.FileStatus == FormatFileStatus.Present
            && format.Fingerprint == value.ObservedFingerprint)
            && value.AnalyzerVersion == PdfAssessmentEngine.AnalyzerVersion
            && value.ScoringModelVersion == PdfAssessmentEngine.ScoringModelVersion
            && value.Features.ResourceProfileVersion == PdfAssessmentEngine.ResourceProfileVersion).ToArray();
        bool usable = compatibleEpubs.Any(value => value.Status == AssessmentStatus.Completed
                && value.Features.ReadableCharacterCount > 0 && !value.Features.AnalysisTruncated)
            || compatiblePdfs.Any(value => value.Status == AssessmentStatus.Completed
                && value.Features.Text.UsefulCharacterCount > 0
                && value.Features.PageAnalysis.FailedPages == 0);
        bool defectiveCompanion = book.Formats.Any(format => format.FileStatus != FormatFileStatus.Present)
            || book.Formats.Any(format => format.Format switch
            {
                "EPUB" => compatibleEpubs.All(value => value.ObservedFingerprint != format.Fingerprint),
                "PDF" => compatiblePdfs.All(value => value.ObservedFingerprint != format.Fingerprint),
                _ => true,
            })
            || compatibleEpubs.Any(value => value.Status != AssessmentStatus.Completed)
            || compatiblePdfs.Any(value => value.Status != AssessmentStatus.Completed);
        bool confirmedUnusable = book.Formats.Count > 0 && book.Formats.All(format =>
            format.FileStatus == FormatFileStatus.Present && format.Fingerprint is not null
            && (format.Format == "EPUB" && compatibleEpubs.Any(value =>
                    value.ObservedFingerprint == format.Fingerprint && value.Status == AssessmentStatus.Disqualified)
                || format.Format == "PDF" && compatiblePdfs.Any(value =>
                    value.ObservedFingerprint == format.Fingerprint && value.Features.OpenStatus == PdfOpenStatus.Unreadable
                    && value.Features.EncryptionStatus == PdfEncryptionStatus.NotEncrypted)));
        bool providerRepair = (providers?.Providers ?? []).Select(value => value.Proposals.GetValueOrDefault(book.Id))
            .Any(value => value?.Status == EditionMetadataProposalStatus.Proposed
                && value.Candidate is not null
                && EditionMetadataApplicationPolicy.ApplicableFields(book, value.Candidate).Any(field =>
                    field == LibraryMetadataField.Title || field == LibraryMetadataField.Authors));
        FinalRecordQualificationEvidence evidence = new(
            titleSupported: agrees,
            authorsSupported: agrees,
            usable ? FinalRecordContentStatus.Usable : confirmedUnusable
                ? FinalRecordContentStatus.ConfirmedUnusable : FinalRecordContentStatus.Incomplete,
            managedLayoutVerified,
            materialIdentityProblem: triage.MaterialIdentityProblem,
            concreteRepairAvailable: finalSubject?.CanApply == true || providerRepair,
            conflicting: conflict || usable && defectiveCompanion || status == AuthorResolutionStatus.Conflicting,
            routes);
        disposition = new(
            disposition.ReasonCodes.Concat(triage.ReasonCodes)
                .Concat((providers?.Providers ?? []).Any(value =>
                    value.Proposals.GetValueOrDefault(book.Id)?.Candidate is { AuthorOrderKnown: false } candidate
                    && !EditionMetadataAuthorWritePolicy.TryPrepare(book, candidate, out _))
                    ? [EditionMetadataAuthorWritePolicy.LegacyAuthorOrderUnavailable] : Array.Empty<string>())
                .Concat(local.Where(value => value.ProblemCode is not null).Select(value => value.ProblemCode!))
                .Concat(defectiveCompanion ? ["RECORD.FORMAT_DEFECT_REQUIRES_REVIEW"] : Array.Empty<string>())
                .Concat(conflict ? ["RECORD.LOCAL_IDENTITY_CONFLICT"] : Array.Empty<string>())
                .Concat(!agrees ? ["RECORD.IDENTITY_EVIDENCE_INCOMPLETE"] : Array.Empty<string>())
                .Concat(!usable && !confirmedUnusable ? ["RECORD.CONTENT_EVIDENCE_INCOMPLETE"] : Array.Empty<string>())
                .Concat(!managedLayoutVerified ? ["RECORD.MANAGED_LAYOUT_UNVERIFIED"] : Array.Empty<string>()),
            disposition.SupportingLabels,
            FinalRecordQualificationPolicy.Assess(evidence),
            evidence);
        return Subject(book, disposition, identityCandidates: pairs);
    }

    private static bool TitleMatches(string left, string right) =>
        MetadataTextNormalizer.TryNormalizeTitle(left, out NormalizedTitle? a)
        && MetadataTextNormalizer.TryNormalizeTitle(right, out NormalizedTitle? b)
        && a == b;

    private static bool IdentityTitleMatches(FrontMatterIdentityCandidate candidate, string title) =>
        TitleMatches(candidate.Title, title) || TitleMatches(candidate.CompleteTitle, title);

    private static bool AuthorsMatch(IEnumerable<string> left, IEnumerable<string> right) =>
        CandidateMetadataNormalizer.HaveExactAuthorIdentity(
            CandidateMetadataNormalizer.AuthorKeys(left, normalizePseudonymAnnotations: false),
            CandidateMetadataNormalizer.AuthorKeys(right, normalizePseudonymAnnotations: false));

    private static QuarantineReviewSubject Subject(
        CalibreBook book,
        FinalRecordDispositionAssessment disposition,
        bool manuallyIncluded = false,
        IEnumerable<FrontMatterIdentityCandidate>? identityCandidates = null) => new(
        book.Id,
        book.Title,
        book.Authors,
        book.Formats.Select(value => new FinalAuthorFormatIdentity(
            value.Format,
            value.ExpectedRelativePath,
            value.FileStatus,
            value.Fingerprint)),
        disposition,
        manuallyIncluded,
        identityCandidates: identityCandidates,
        inputIdentity: FinalRecordInputIdentity.Create(book));

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
        destinationIdentity ?? source.DestinationIdentity,
        source.RetainedInputCount,
        source.VerifiedMovedCount);
}
