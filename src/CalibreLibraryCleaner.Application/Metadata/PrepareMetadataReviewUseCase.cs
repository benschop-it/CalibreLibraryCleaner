using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public interface IMetadataReviewDecisionStore
{
    Task<IReadOnlyList<MetadataReviewDecision>> ReadAsync(
        string libraryRoot,
        CancellationToken cancellationToken);

    Task WriteAsync(
        string libraryRoot,
        IReadOnlyList<MetadataReviewDecision> decisions,
        CancellationToken cancellationToken);
}

public sealed record MetadataReviewWorkspace
{
    public MetadataReviewWorkspace(
        string libraryRoot,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        IEnumerable<ReviewedMetadataSubject> subjects,
        bool persistenceAvailable,
        string? persistenceProblemCode = null,
        int deferredQueryCount = 0,
        EditionMetadataProvidersBatchResult? providerResults = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(subjects);
        ReviewedMetadataSubject[] values = subjects.OrderBy(value => value.Subject.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (values.Select(value => value.Subject.Id).Distinct().Count() != values.Length
            || persistenceAvailable == !string.IsNullOrWhiteSpace(persistenceProblemCode)
            || persistenceProblemCode is { Length: > 128 }
            || deferredQueryCount < 0)
            throw new ArgumentException("Metadata review workspace is invalid.");
        LibraryRoot = libraryRoot;
        GenerationId = generationId;
        Revision = revision;
        Subjects = new ReadOnlyCollection<ReviewedMetadataSubject>(values);
        PersistenceAvailable = persistenceAvailable;
        PersistenceProblemCode = persistenceProblemCode;
        DeferredQueryCount = deferredQueryCount;
        ProviderResults = providerResults;
    }

    public string LibraryRoot { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
    public IReadOnlyList<ReviewedMetadataSubject> Subjects { get; }
    public bool PersistenceAvailable { get; }
    public string? PersistenceProblemCode { get; }
    public int DeferredQueryCount { get; }
    public EditionMetadataProvidersBatchResult? ProviderResults { get; }
}

public sealed class PrepareMetadataReviewUseCase(
    ResolveEditionMetadataProposalsUseCase resolveProposals,
    IMetadataReviewDecisionStore decisionStore)
{
    public async Task<MetadataReviewWorkspace> ExecuteAsync(
        LibraryState state,
        IReadOnlyList<MetadataReviewKeeperSelection> keeperSelections,
        IProgress<EditionMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken,
        FinalAuthorReviewWorkspace? identityReview = null,
        IReadOnlyCollection<CalibreBookId>? repairBookIds = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(keeperSelections);
        HashSet<CalibreBookId>? repairScope = repairBookIds?.ToHashSet();
        if (identityReview is not null && (repairScope is null || repairScope.Count == 0)
            || identityReview is null && repairScope is not null
            || repairScope is not null && (repairScope.Count != repairBookIds!.Count
                || !repairScope.IsSubsetOf(state.Snapshot.Books.Select(value => value.Id))))
            throw new ArgumentException("Identity repairs require distinct selected current record IDs.", nameof(repairBookIds));
        if (!state.IsAuthoritative || !state.IsWorkflowCheckpointCurrent
            || state.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.CandidateCleanupCompleted
                && !(state.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.Completed
                    && identityReview is not null))
            throw new InvalidOperationException("Metadata review requires completed Candidate cleanup state.");
        if (identityReview is not null
            && (identityReview.GenerationId != state.GenerationId || identityReview.Revision != state.Revision
                || identityReview.LibraryRoot != state.Snapshot.Identity.LibraryRoot))
            throw new InvalidOperationException("Identity repair evidence is stale.");
        CalibreBook[] demand = state.Snapshot.Books
            .Where(book => repairScope is null || repairScope.Contains(book.Id)).ToArray();
        Dictionary<CalibreBookId, FrontMatterIdentityCandidate> localIdentities = [];
        if (identityReview is not null)
        {
            ILookup<FormatFileFingerprint, FrontMatterAuthorObservation> observations =
                identityReview.FrontMatterObservations.ToLookup(value => value.Fingerprint);
            foreach (CalibreBook book in demand)
            {
                FrontMatterIdentityCandidate[] candidates = book.Formats
                    .Where(value => value.Fingerprint is not null)
                    .SelectMany(format => observations[format.Fingerprint!].Where(value =>
                        value.Format == format.Format
                        && value.ExtractorVersion == (format.Format == "EPUB"
                            ? FrontMatterAuthorVersions.EpubExtractor : FrontMatterAuthorVersions.PdfExtractor)
                        && value.LimitsVersion == FrontMatterAuthorVersions.Limits
                        && value.Coverage == FinalRecordRouteOutcome.CompletedWithEvidence))
                    .SelectMany(value => value.IdentityCandidates.Where(candidate => candidate.ExplicitCredit))
                    .DistinctBy(value => (value.Title, Authors: string.Join('\u001f', value.Authors))).ToArray();
                if (candidates.Length == 1) localIdentities.Add(book.Id, candidates[0]);
            }
        }
        EditionMetadataProvidersBatchResult providerResults = await resolveProposals.ExecuteAsync(
            demand, progress, cancellationToken, localIdentities).ConfigureAwait(false);
        Dictionary<CalibreBookId, EditionMetadataProposal> localRepairs = [];
        EditionMetadataProviderIdentity localSource = new("local-front-matter",
            FrontMatterIdentityExtraction.PolicyVersion + "+" + AuthorBylinePolicy.PolicyVersion);
        foreach (CalibreBook book in demand)
        {
            if (!localIdentities.TryGetValue(book.Id, out FrontMatterIdentityCandidate? identity)
                || (identity.Title == book.Title || identity.CompleteTitle == book.Title)
                    && CandidateMetadataNormalizer.HaveExactAuthorIdentity(
                        CandidateMetadataNormalizer.AuthorKeys(identity.Authors),
                        CandidateMetadataNormalizer.AuthorKeys(book.Authors.Select(value => value.Name))))
                continue;
            string sourceId = $"local-identity:{book.Id.Value}";
            localRepairs.Add(book.Id, new(
                book.Id, localSource, EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors,
                state.Snapshot.ScannedAt, EditionMetadataProposalStatus.Proposed,
                new(sourceId, sourceId, identity.BibliographicTitle, identity.Authors), 1,
                identity.BibliographicTitle == identity.CompleteTitle
                    ? ["METADATA.LOCAL.PAIRED_IDENTITY_REQUIRES_REVIEW"]
                    : ["METADATA.LOCAL.PAIRED_IDENTITY_REQUIRES_REVIEW", "METADATA.LOCAL.SUBTITLE_RETAINED_IN_EVIDENCE"]));
        }
        if (localRepairs.Count > 0)
            providerResults = new([
                .. providerResults.Providers,
                new(localSource, localRepairs, 0, 0, 0, 0, 0, localRepairs.Count, 0, false, true),
            ], providerResults.InputIdentities);
        IReadOnlyList<MetadataReviewSubject> subjects = BuildMetadataReviewSubjectsUseCase.Execute(
            state.Snapshot, providerResults, keeperSelections, repairScope);
        int deferredQueryCount = providerResults.Providers
            .Where(value => value.Enabled && value.RequestLimitReached)
            .Sum(value => Math.Max(0, value.QueryCount - value.CompletedQueryCount));
        IReadOnlyList<MetadataReviewDecision> decisions;
        try
        {
            decisions = await decisionStore.ReadAsync(
                state.Snapshot.Identity.LibraryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return Workspace(
                Review([]),
                persistenceAvailable: false,
                "METADATA_REVIEW.DECISION_READ_FAILED",
                deferredQueryCount);
        }
        IReadOnlyList<ReviewedMetadataSubject> reviewed = Review(decisions);
        MetadataReviewWorkspace workspace = Workspace(
            reviewed, persistenceAvailable: true, null, deferredQueryCount);
        try
        {
            await decisionStore.WriteAsync(
                workspace.LibraryRoot, MergeDecisions(workspace, decisions), cancellationToken).ConfigureAwait(false);
            return workspace;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return Workspace(
                reviewed,
                persistenceAvailable: false,
                "METADATA_REVIEW.DECISION_WRITE_FAILED",
                deferredQueryCount);
        }

        IReadOnlyList<ReviewedMetadataSubject> Review(IReadOnlyList<MetadataReviewDecision> saved)
        {
            ReviewedMetadataSubject[] values = ApplyConvergence(state.Snapshot,
                MetadataReviewDecisionPolicy.Reconcile(subjects, state.GenerationId, state.Revision, saved));
            return identityReview is null ? values : values.Select(value => new ReviewedMetadataSubject(
                value.Subject, false, false, null, value.IsAlreadyApplied,
                requiresReview: !value.IsAlreadyApplied && value.Subject.Proposal.Candidate is not null)).ToArray();
        }

        MetadataReviewWorkspace Workspace(
            IEnumerable<ReviewedMetadataSubject> reviewedSubjects,
            bool persistenceAvailable,
            string? problemCode,
            int deferredQueries) => new(
            state.Snapshot.Identity.LibraryRoot,
            state.GenerationId,
            state.Revision,
            reviewedSubjects,
            persistenceAvailable,
            problemCode,
            deferredQueries,
            providerResults);
    }

    public Task<MetadataReviewWorkspace> SetApplyAsync(
        MetadataReviewWorkspace workspace,
        MetadataReviewSubjectId subjectId,
        bool apply,
        CancellationToken cancellationToken) => SetApplyAsync(workspace, [subjectId], apply, cancellationToken);

    public async Task<MetadataReviewWorkspace> SetAllApplyAsync(
        MetadataReviewWorkspace workspace,
        bool apply,
        CancellationToken cancellationToken) => await SetApplyAsync(
            workspace,
            workspace.Subjects.Where(value =>
                    value.Subject.Proposal.Confidence != FusedEditionMetadataConfidence.Unavailable
                    && !value.IsAlreadyApplied)
                .Select(value => value.Subject.Id)
                .ToArray(),
            apply,
            cancellationToken).ConfigureAwait(false);

    public async Task<MetadataReviewWorkspace> SetApplyAsync(
        MetadataReviewWorkspace workspace,
        IReadOnlyCollection<MetadataReviewSubjectId> subjectIds,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(subjectIds);
        HashSet<MetadataReviewSubjectId> selected = subjectIds.ToHashSet();
        if (selected.Count != subjectIds.Count
            || selected.Any(subjectId => workspace.Subjects.All(value => value.Subject.Id != subjectId)))
            throw new ArgumentException("Metadata review selections are not current.", nameof(subjectIds));
        if (selected.Count == 0) return workspace;
        ReviewedMetadataSubject[] reviewed = workspace.Subjects.Select(current =>
        {
            if (!selected.Contains(current.Subject.Id)
                || current.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Unavailable
                || current.IsAlreadyApplied)
                return current;
            MetadataReviewDecision? decision = MetadataReviewDecisionPolicy.CreateOverride(
                current.Subject, workspace.GenerationId, workspace.Revision, apply, DefaultApply(current));
            return decision is null
                ? new ReviewedMetadataSubject(
                    current.Subject, DefaultApply(current), false, null, requiresReview: current.RequiresReview)
                : new ReviewedMetadataSubject(
                    current.Subject, decision.Apply, true, decision.Key, requiresReview: current.RequiresReview);
        }).ToArray();
        MetadataReviewWorkspace next = new(
            workspace.LibraryRoot,
            workspace.GenerationId,
            workspace.Revision,
            reviewed,
            workspace.PersistenceAvailable,
            workspace.PersistenceProblemCode,
            workspace.DeferredQueryCount,
            workspace.ProviderResults);
        try
        {
            IReadOnlyList<MetadataReviewDecision> existing = await decisionStore.ReadAsync(
                next.LibraryRoot, cancellationToken).ConfigureAwait(false);
            await decisionStore.WriteAsync(
                next.LibraryRoot, MergeDecisions(next, existing), cancellationToken).ConfigureAwait(false);
            return new(next.LibraryRoot, next.GenerationId, next.Revision, next.Subjects,
                persistenceAvailable: true, deferredQueryCount: next.DeferredQueryCount, providerResults: next.ProviderResults);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return new(
                next.LibraryRoot,
                next.GenerationId,
                next.Revision,
                next.Subjects,
                persistenceAvailable: false,
                "METADATA_REVIEW.DECISION_WRITE_FAILED",
                next.DeferredQueryCount,
                next.ProviderResults);
        }
    }

    private static ReviewedMetadataSubject[] ApplyConvergence(
        LibrarySnapshot snapshot,
        IReadOnlyList<ReviewedMetadataSubject> reviewed)
    {
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        return reviewed.Select(value =>
        {
            if (value.Subject.Proposal.Candidate is not { } candidate) return value;
            CalibreBook book = books[value.Subject.TargetBookId];
            if (EditionMetadataApplicationPolicy.ApplicableFields(book, candidate).Count == 0)
                return new ReviewedMetadataSubject(value.Subject, false, false, null, isAlreadyApplied: true);
            if (value.Subject.Proposal.IsSelectedByDefault
                && !EditionMetadataApplicationPolicy.CanApplyAutomatically(book, candidate))
                return value.IsOverride
                    ? new ReviewedMetadataSubject(
                        value.Subject, value.Apply, true, value.DecisionKey, requiresReview: true)
                    : new ReviewedMetadataSubject(value.Subject, false, false, null, requiresReview: true);
            return value;
        })
            .ToArray();
    }

    private static bool DefaultApply(ReviewedMetadataSubject subject) =>
        subject.Subject.Proposal.IsSelectedByDefault && !subject.RequiresReview;

    public static MetadataReviewWorkspace Retarget(
        MetadataReviewWorkspace workspace,
        UnifiedCandidateGroupId groupId,
        CalibreBookId targetBookId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ReviewedMetadataSubject[] reviewed = workspace.Subjects.Select(value =>
            value.Subject.UnifiedGroupId == groupId
                ? new ReviewedMetadataSubject(
                    value.Subject.Retarget(targetBookId), value.Apply, value.IsOverride, value.DecisionKey)
                : value).ToArray();
        if (!reviewed.Any(value => value.Subject.UnifiedGroupId == groupId))
            throw new ArgumentException("The metadata review group is not current.", nameof(groupId));
        return new(
            workspace.LibraryRoot,
            workspace.GenerationId,
            workspace.Revision,
            reviewed,
            workspace.PersistenceAvailable,
            workspace.PersistenceProblemCode,
            workspace.DeferredQueryCount,
            workspace.ProviderResults);
    }

    private static MetadataReviewDecision[] MergeDecisions(
        MetadataReviewWorkspace workspace,
        IReadOnlyList<MetadataReviewDecision> previous)
    {
        HashSet<MetadataReviewSubjectId> subjects = workspace.Subjects.Select(value => value.Subject.Id).ToHashSet();
        return previous.Where(value => !subjects.Contains(value.Key.SubjectId))
            .Concat(CurrentDecisions(workspace))
            .OrderBy(value => value.Key.SubjectId.Value, StringComparer.Ordinal).ToArray();
    }

    private static MetadataReviewDecision[] CurrentDecisions(MetadataReviewWorkspace workspace) =>
        workspace.Subjects.Where(value => value.IsOverride && value.DecisionKey is not null)
            .Select(value => new MetadataReviewDecision(value.DecisionKey!, value.Apply))
            .OrderBy(value => value.Key.SubjectId.Value, StringComparer.Ordinal).ToArray();

    private static bool IsPersistenceFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or InvalidOperationException
        or ArgumentException
        or OverflowException;
}
