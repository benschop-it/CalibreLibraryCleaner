using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

[Flags]
public enum FinalAuthorEvidenceCoverage
{
    None = 0,
    EpubPackageMetadata = 1 << 0,
    PdfDocumentMetadata = 1 << 1,
    EpubFrontMatter = 1 << 2,
    PdfFrontMatter = 1 << 3,
}

public sealed record FinalAuthorFormatIdentity(
    string Format,
    string ExpectedRelativePath,
    FormatFileStatus FileStatus,
    FormatFileFingerprint? Fingerprint);

public sealed record FinalAuthorReviewSubject
{
    public FinalAuthorReviewSubject(
        CalibreBookId bookId,
        string currentTitle,
        IEnumerable<BookAuthor> currentAuthors,
        IEnumerable<FinalAuthorFormatIdentity> formats,
        AuthorResolution resolution,
        FinalAuthorEvidenceCoverage evidenceCoverage,
        bool needsInspection,
        IEnumerable<FrontMatterAuthorObservation>? frontMatterEvidence = null,
        bool? apply = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTitle);
        ArgumentNullException.ThrowIfNull(currentAuthors);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(resolution);
        const FinalAuthorEvidenceCoverage allCoverage =
            FinalAuthorEvidenceCoverage.EpubPackageMetadata
            | FinalAuthorEvidenceCoverage.PdfDocumentMetadata
            | FinalAuthorEvidenceCoverage.EpubFrontMatter
            | FinalAuthorEvidenceCoverage.PdfFrontMatter;
        if ((evidenceCoverage & ~allCoverage) != 0
            || resolution.Status == AuthorResolutionStatus.Known && resolution.ProposalSource == AuthorProposalSource.None
            || needsInspection && resolution.Status == AuthorResolutionStatus.Known)
            throw new ArgumentException("Final author review subject is invalid.");
        BookAuthor[] authors = currentAuthors.ToArray();
        FinalAuthorFormatIdentity[] formatValues = formats
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        BookId = bookId;
        CurrentTitle = currentTitle;
        CurrentAuthors = new ReadOnlyCollection<BookAuthor>(authors);
        Formats = new ReadOnlyCollection<FinalAuthorFormatIdentity>(formatValues);
        Resolution = resolution;
        EvidenceCoverage = evidenceCoverage;
        NeedsInspection = needsInspection;
        FrontMatterEvidence = new ReadOnlyCollection<FrontMatterAuthorObservation>(
            (frontMatterEvidence ?? []).ToArray());
        CanApply = resolution.ProposalSource != AuthorProposalSource.None;
        Apply = apply ?? resolution.SelectedByDefault;
        if (Apply && !CanApply)
            throw new ArgumentException("A final author subject without a proposal cannot be applied.");
    }

    public CalibreBookId BookId { get; }
    public string CurrentTitle { get; }
    public IReadOnlyList<BookAuthor> CurrentAuthors { get; }
    public IReadOnlyList<FinalAuthorFormatIdentity> Formats { get; }
    public AuthorResolution Resolution { get; }
    public FinalAuthorEvidenceCoverage EvidenceCoverage { get; }
    public bool NeedsInspection { get; }
    public IReadOnlyList<FrontMatterAuthorObservation> FrontMatterEvidence { get; }
    public bool CanApply { get; }
    public bool Apply { get; }

    public FinalAuthorReviewSubject WithApply(bool apply) => new(
        BookId,
        CurrentTitle,
        CurrentAuthors,
        Formats,
        Resolution,
        EvidenceCoverage,
        NeedsInspection,
        FrontMatterEvidence,
        apply);
}

public sealed record FinalAuthorReviewWorkspace
{
    public FinalAuthorReviewWorkspace(
        string libraryRoot,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        IEnumerable<FinalAuthorReviewSubject> subjects,
        int screenedCount,
        int knownCount,
        int compatibleEvidenceCount,
        bool appliedOnlineProvenanceAvailable,
        int metadataCacheHitCount = 0,
        int metadataInspectionCount = 0,
        IEnumerable<EmbeddedAuthorMetadataObservation>? embeddedObservations = null,
        IEnumerable<FrontMatterAuthorObservation>? frontMatterObservations = null,
        int frontMatterCacheHitCount = 0,
        int frontMatterInspectionCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(subjects);
        FinalAuthorReviewSubject[] values = subjects.OrderBy(value => value.BookId.Value).ToArray();
        if (values.Select(value => value.BookId).Distinct().Count() != values.Length
            || screenedCount < 0
            || knownCount < 0
            || knownCount + values.Length != screenedCount
            || compatibleEvidenceCount < 0
            || metadataCacheHitCount < 0
            || metadataInspectionCount < 0
            || frontMatterCacheHitCount < 0
            || frontMatterInspectionCount < 0)
            throw new ArgumentException("Final author review workspace is invalid.");
        LibraryRoot = libraryRoot;
        GenerationId = generationId;
        Revision = revision;
        Subjects = new ReadOnlyCollection<FinalAuthorReviewSubject>(values);
        ScreenedCount = screenedCount;
        KnownCount = knownCount;
        CompatibleEvidenceCount = compatibleEvidenceCount;
        AppliedOnlineProvenanceAvailable = appliedOnlineProvenanceAvailable;
        MetadataCacheHitCount = metadataCacheHitCount;
        MetadataInspectionCount = metadataInspectionCount;
        EmbeddedObservations = new ReadOnlyCollection<EmbeddedAuthorMetadataObservation>(
            (embeddedObservations ?? []).ToArray());
        FrontMatterObservations = new ReadOnlyCollection<FrontMatterAuthorObservation>(
            (frontMatterObservations ?? []).ToArray());
        FrontMatterCacheHitCount = frontMatterCacheHitCount;
        FrontMatterInspectionCount = frontMatterInspectionCount;
        SuspicionPolicyVersion = AuthorSuspicionPolicy.PolicyVersion;
        SourcePreservationPolicyVersion = AuthorSourcePreservationPolicy.PolicyVersion;
    }

    public string LibraryRoot { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
    public IReadOnlyList<FinalAuthorReviewSubject> Subjects { get; }
    public int ScreenedCount { get; }
    public int KnownCount { get; }
    public int SuspectCount => Subjects.Count(value => value.Resolution.Status == AuthorResolutionStatus.Suspect);
    public int UnresolvedCount => Subjects.Count(value => value.Resolution.Status == AuthorResolutionStatus.Unresolved);
    public int ConflictingCount => Subjects.Count(value => value.Resolution.Status == AuthorResolutionStatus.Conflicting);
    public int CompatibleEvidenceCount { get; }
    public int InspectionDemandCount => Subjects.Count(value => value.NeedsInspection);
    public bool AppliedOnlineProvenanceAvailable { get; }
    public int MetadataCacheHitCount { get; }
    public int MetadataInspectionCount { get; }
    public IReadOnlyList<EmbeddedAuthorMetadataObservation> EmbeddedObservations { get; }
    public IReadOnlyList<FrontMatterAuthorObservation> FrontMatterObservations { get; }
    public int FrontMatterCacheHitCount { get; }
    public int FrontMatterInspectionCount { get; }
    public string SuspicionPolicyVersion { get; }
    public string SourcePreservationPolicyVersion { get; }
}

public static class PrepareFinalAuthorReviewUseCase
{
    public static FinalAuthorReviewWorkspace AddReviewedBibliographicRepairs(
        LibraryState state,
        FinalAuthorReviewWorkspace current,
        MetadataReviewWorkspace reviewedMetadata)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(reviewedMetadata);
        if (current.GenerationId != state.GenerationId || current.Revision != state.Revision
            || reviewedMetadata.GenerationId != state.GenerationId)
            throw new InvalidOperationException("Reviewed identity repair evidence is stale.");
        Dictionary<CalibreBookId, FinalAuthorReviewSubject> subjects =
            current.Subjects.ToDictionary(value => value.BookId);
        Dictionary<CalibreBookId, CalibreBook> books = state.Snapshot.Books.ToDictionary(value => value.Id);
        foreach (ReviewedMetadataSubject reviewed in reviewedMetadata.Subjects.Where(value => value.Apply))
        {
            EditionMetadataCandidate? candidate = reviewed.Subject.Proposal.Candidate;
            if (candidate is null || !books.TryGetValue(reviewed.Subject.TargetBookId, out CalibreBook? book)
                || !string.Equals(book.Title, candidate.Title, StringComparison.Ordinal)
                || !EditionMetadataAuthorWritePolicy.TryPrepare(book, candidate, out IReadOnlyList<string> names)
                || book.Authors.Select(value => value.Name).SequenceEqual(names))
                continue;
            EditionMetadataProviderIdentity provider = reviewed.Subject.Proposal.PrimaryProvider!;
            AuthorResolution resolution = new(
                AuthorResolutionStatus.Suspect, CurrentAuthorProtection.Unverified,
                ["AUTHOR.REVIEWED_BIBLIOGRAPHIC_REPAIR"],
                [new(AuthorEvidenceSource.ReviewedBibliographicRepair,
                    $"{provider.Id}:{provider.Version}:{candidate.EditionId}", names)],
                AuthorProposalSource.ReviewedBibliographicRepair, names);
            subjects[book.Id] = new(
                book.Id, book.Title, book.Authors,
                book.Formats.Select(value => new FinalAuthorFormatIdentity(
                    value.Format, value.ExpectedRelativePath, value.FileStatus, value.Fingerprint)),
                resolution, FinalAuthorEvidenceCoverage.None, needsInspection: false, apply: true);
        }
        return new(
            current.LibraryRoot, current.GenerationId, current.Revision, subjects.Values,
            current.ScreenedCount, current.ScreenedCount - subjects.Count,
            current.CompatibleEvidenceCount, current.AppliedOnlineProvenanceAvailable,
            current.MetadataCacheHitCount, current.MetadataInspectionCount,
            current.EmbeddedObservations, current.FrontMatterObservations,
            current.FrontMatterCacheHitCount, current.FrontMatterInspectionCount);
    }

    public static FinalAuthorReviewWorkspace SetApply(
        FinalAuthorReviewWorkspace workspace,
        IReadOnlyCollection<CalibreBookId> bookIds,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(bookIds);
        HashSet<CalibreBookId> selected = bookIds.ToHashSet();
        if (selected.Count != bookIds.Count
            || selected.Any(id => workspace.Subjects.All(value => value.BookId != id)))
            throw new ArgumentException("Final author review selections are not current.", nameof(bookIds));
        FinalAuthorReviewSubject[] subjects = workspace.Subjects.Select(value =>
            selected.Contains(value.BookId) && value.CanApply ? value.WithApply(apply) : value).ToArray();
        return Copy(workspace, subjects);
    }

    public static FinalAuthorReviewWorkspace SetAllApply(
        FinalAuthorReviewWorkspace workspace,
        bool apply) => SetApply(
        workspace,
        workspace.Subjects.Where(value => value.CanApply).Select(value => value.BookId).ToArray(),
        apply);

    public static FinalAuthorReviewWorkspace Execute(
        LibraryState state,
        IReadOnlyList<EmbeddedAuthorMetadataObservation>? embeddedObservations = null,
        int metadataCacheHitCount = 0,
        int metadataInspectionCount = 0,
        IReadOnlyList<FrontMatterAuthorObservation>? frontMatterObservations = null,
        int frontMatterCacheHitCount = 0,
        int frontMatterInspectionCount = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsAuthoritative
            || !state.IsWorkflowCheckpointCurrent
            || state.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.Completed)
            throw new InvalidOperationException("Final author review requires completed authoritative workflow state.");

        List<FinalAuthorReviewSubject> subjects = [];
        IReadOnlyList<EmbeddedAuthorMetadataObservation> observations = embeddedObservations ?? [];
        IReadOnlyList<FrontMatterAuthorObservation> frontMatter = frontMatterObservations ?? [];
        int knownCount = 0;
        int compatibleEvidenceCount = 0;
        foreach (CalibreBook book in state.Snapshot.Books.OrderBy(value => value.Id.Value))
        {
            AuthorSuspicionAssessment suspicion = AuthorSuspicionPolicy.Assess(
                book.Title, book.Authors.Select(value => value.Name));
            bool normalizationCandidate = PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
                    book.Authors, out _, out _)
                || AuthorDisplayNameHarmonizationPolicy.TryHarmonizeBook(
                    book.Authors, out _, out _)
                || AuthorPseudonymAnnotationPolicy.TryNormalizeBook(book.Authors, out _);
            if (suspicion.Status == AuthorResolutionStatus.Known && !normalizationCandidate)
            {
                knownCount++;
                continue;
            }

            List<AuthorEvidenceObservation> evidence = [];
            FinalAuthorEvidenceCoverage coverage = FinalAuthorEvidenceCoverage.None;
            foreach (EpubAssessment assessment in state.Snapshot.EpubAssessments.Where(
                         value => value.CalibreBookId == book.Id))
            {
                if (!IsCompatible(book, assessment)) continue;
                compatibleEvidenceCount++;
                coverage |= FinalAuthorEvidenceCoverage.EpubPackageMetadata;
                evidence.Add(new(
                    AuthorEvidenceSource.EpubPackageMetadata,
                    EvidenceIdentity(assessment.Format, assessment.ObservedFingerprint!, assessment.AnalyzerVersion.Value),
                    assessment.Features.Authors));
            }
            foreach (PdfAssessment assessment in state.Snapshot.PdfAssessments.Where(
                         value => value.CalibreBookId == book.Id))
            {
                if (!IsCompatible(book, assessment)) continue;
                compatibleEvidenceCount++;
                coverage |= FinalAuthorEvidenceCoverage.PdfDocumentMetadata;
                evidence.Add(new(
                    AuthorEvidenceSource.PdfDocumentMetadata,
                    EvidenceIdentity(assessment.Format, assessment.ObservedFingerprint!, assessment.AnalyzerVersion.Value),
                    assessment.Features.Metadata.Author.Status == PdfMetadataValueStatus.Present
                        ? [assessment.Features.Metadata.Author.Value!]
                        : []));
            }
            foreach (EmbeddedAuthorMetadataObservation observation in observations.Where(value =>
                         MatchesObservation(book, value)))
            {
                compatibleEvidenceCount++;
                AuthorEvidenceSource source = observation.Key.Format == "EPUB"
                    ? AuthorEvidenceSource.EpubPackageMetadata
                    : AuthorEvidenceSource.PdfDocumentMetadata;
                coverage |= observation.Key.Format == "EPUB"
                    ? FinalAuthorEvidenceCoverage.EpubPackageMetadata
                    : FinalAuthorEvidenceCoverage.PdfDocumentMetadata;
                evidence.Add(new(
                    source,
                    EvidenceIdentity(
                        observation.Key.Format,
                        observation.Key.Fingerprint,
                        observation.Key.ExtractorVersion),
                    observation.Authors));
            }
            FrontMatterAuthorObservation[] bookFrontMatter = frontMatter.Where(value =>
                MatchesFrontMatterObservation(book, value)).ToArray();
            foreach (FrontMatterAuthorObservation observation in bookFrontMatter)
            {
                compatibleEvidenceCount++;
                coverage |= observation.Format == "EPUB"
                    ? FinalAuthorEvidenceCoverage.EpubFrontMatter
                    : FinalAuthorEvidenceCoverage.PdfFrontMatter;
                foreach (FrontMatterAuthorCandidate candidate in observation.Candidates.Where(value =>
                             value.Role == AuthorCreditRole.Author))
                {
                    evidence.Add(new(
                        observation.Format == "EPUB"
                            ? AuthorEvidenceSource.EpubFrontMatter
                            : AuthorEvidenceSource.PdfFrontMatter,
                        EvidenceIdentity(
                            observation.Format,
                            observation.Fingerprint,
                            observation.ExtractorVersion),
                        candidate.Names));
                }
            }

            AuthorResolution resolution = AuthorSourcePreservationPolicy.Resolve(
                book.Title,
                book.Authors,
                verifiedAppliedOnlineAuthors: book.AppliedAuthorProvenance is not null,
                evidence);
            if (resolution.Status == AuthorResolutionStatus.Known
                && resolution.ProposalSource == AuthorProposalSource.None)
            {
                knownCount++;
                continue;
            }
            bool needsInspection = resolution.Status is AuthorResolutionStatus.Unresolved
                or AuthorResolutionStatus.Conflicting
                || resolution.Status == AuthorResolutionStatus.Suspect
                && resolution.ProposalSource == AuthorProposalSource.None;
            BookFormat[] inspectableFormats = book.Formats.Where(value =>
                value.FileStatus == FormatFileStatus.Present
                && value.Fingerprint is not null
                && value.Format is "EPUB" or "PDF").ToArray();
            bool frontMatterExhausted = inspectableFormats.Length > 0
                && inspectableFormats.All(format => bookFrontMatter.Any(value =>
                    value.Format == format.Format && value.Fingerprint == format.Fingerprint
                    && value.Coverage is FinalRecordRouteOutcome.CompletedWithEvidence
                        or FinalRecordRouteOutcome.CompletedWithoutEvidence));
            needsInspection &= !frontMatterExhausted;
            subjects.Add(new(
                book.Id,
                book.Title,
                book.Authors,
                book.Formats.Select(value => new FinalAuthorFormatIdentity(
                    value.Format,
                    value.ExpectedRelativePath,
                    value.FileStatus,
                    value.Fingerprint)),
                resolution,
                coverage,
                needsInspection,
                bookFrontMatter));
        }

        return new(
            state.Snapshot.Identity.LibraryRoot,
            state.GenerationId,
            state.Revision,
            subjects,
            state.Snapshot.Books.Count,
            knownCount,
            compatibleEvidenceCount,
            appliedOnlineProvenanceAvailable: state.Snapshot.Books.Any(
                value => value.AppliedAuthorProvenance is not null),
            metadataCacheHitCount,
            metadataInspectionCount,
            observations,
            frontMatter,
            frontMatterCacheHitCount,
            frontMatterInspectionCount);
    }

    private static bool IsCompatible(CalibreBook book, EpubAssessment assessment) =>
        assessment.ObservedFingerprint is not null
        && assessment.AnalyzerVersion == EpubAssessmentEngine.AnalyzerVersion
        && assessment.ScoringModelVersion == EpubAssessmentEngine.ScoringModelVersion
        && assessment.Features.PackageParsed
        && assessment.Features.AvailableFacets.HasFlag(EpubAssessmentFacet.Metadata)
        && MatchingFormat(book, assessment.Format, assessment.ExpectedRelativePath, assessment.ObservedFingerprint) is not null;

    private static bool IsCompatible(CalibreBook book, PdfAssessment assessment) =>
        assessment.ObservedFingerprint is not null
        && assessment.ObservedObservation is not null
        && assessment.AnalyzerVersion == PdfAssessmentEngine.AnalyzerVersion
        && assessment.ScoringModelVersion == PdfAssessmentEngine.ScoringModelVersion
        && assessment.Features.ResourceProfileVersion == PdfAssessmentEngine.ResourceProfileVersion
        && assessment.Features.OpenStatus == PdfOpenStatus.Opened
        && MatchingFormat(book, assessment.Format, assessment.ExpectedRelativePath, assessment.ObservedFingerprint)
            is { Observation: not null } format
        && format.Observation == assessment.ObservedObservation;

    private static BookFormat? MatchingFormat(
        CalibreBook book,
        string format,
        string expectedRelativePath,
        FormatFileFingerprint fingerprint) => book.Formats.SingleOrDefault(value =>
        value.FileStatus == FormatFileStatus.Present
        && string.Equals(value.Format, format, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            value.ExpectedRelativePath.Replace('\\', '/'), expectedRelativePath.Replace('\\', '/'),
            StringComparison.Ordinal)
        && value.Fingerprint == fingerprint);

    private static bool MatchesObservation(
        CalibreBook book,
        EmbeddedAuthorMetadataObservation observation) => book.Formats.Any(value =>
        value.FileStatus == FormatFileStatus.Present
        && string.Equals(value.Format, observation.Key.Format, StringComparison.OrdinalIgnoreCase)
        && value.Fingerprint == observation.Key.Fingerprint);

    private static bool MatchesFrontMatterObservation(
        CalibreBook book,
        FrontMatterAuthorObservation observation) =>
        observation.ExtractorVersion == (observation.Format == "EPUB"
            ? FrontMatterAuthorVersions.EpubExtractor : FrontMatterAuthorVersions.PdfExtractor)
        && observation.LimitsVersion == FrontMatterAuthorVersions.Limits
        && book.Formats.Any(value =>
        value.FileStatus == FormatFileStatus.Present
        && string.Equals(value.Format, observation.Format, StringComparison.OrdinalIgnoreCase)
        && value.Fingerprint == observation.Fingerprint);

    private static string EvidenceIdentity(
        string format,
        FormatFileFingerprint fingerprint,
        string analyzerVersion) => $"{format}:{fingerprint.Sha256.Value}:{analyzerVersion}";

    private static FinalAuthorReviewWorkspace Copy(
        FinalAuthorReviewWorkspace source,
        IEnumerable<FinalAuthorReviewSubject> subjects) => new(
        source.LibraryRoot,
        source.GenerationId,
        source.Revision,
        subjects,
        source.ScreenedCount,
        source.KnownCount,
        source.CompatibleEvidenceCount,
        source.AppliedOnlineProvenanceAvailable,
        source.MetadataCacheHitCount,
        source.MetadataInspectionCount,
        source.EmbeddedObservations,
        source.FrontMatterObservations,
        source.FrontMatterCacheHitCount,
        source.FrontMatterInspectionCount);
}
