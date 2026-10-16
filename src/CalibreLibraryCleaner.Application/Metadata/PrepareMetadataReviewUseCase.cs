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
        int deferredQueryCount = 0)
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
    }

    public string LibraryRoot { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
    public IReadOnlyList<ReviewedMetadataSubject> Subjects { get; }
    public bool PersistenceAvailable { get; }
    public string? PersistenceProblemCode { get; }
    public int DeferredQueryCount { get; }
}

public sealed class PrepareMetadataReviewUseCase(
    ResolveEditionMetadataProposalsUseCase resolveProposals,
    IMetadataReviewDecisionStore decisionStore)
{
    public async Task<MetadataReviewWorkspace> ExecuteAsync(
        LibraryState state,
        IReadOnlyList<MetadataReviewKeeperSelection> keeperSelections,
        IProgress<EditionMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(keeperSelections);
        if (!state.IsAuthoritative || !state.IsWorkflowCheckpointCurrent
            || state.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.CandidateCleanupCompleted)
            throw new InvalidOperationException("Metadata review requires completed Candidate cleanup state.");
        EditionMetadataProvidersBatchResult providerResults = await resolveProposals.ExecuteAsync(
            state.Snapshot.Books, progress, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MetadataReviewSubject> subjects = BuildMetadataReviewSubjectsUseCase.Execute(
            state.Snapshot, providerResults, keeperSelections);
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
                ApplyConvergence(state.Snapshot, MetadataReviewDecisionPolicy.Reconcile(
                    subjects, state.GenerationId, state.Revision, [])),
                persistenceAvailable: false,
                "METADATA_REVIEW.DECISION_READ_FAILED",
                deferredQueryCount);
        }
        IReadOnlyList<ReviewedMetadataSubject> reviewed = ApplyConvergence(
            state.Snapshot,
            MetadataReviewDecisionPolicy.Reconcile(
                subjects, state.GenerationId, state.Revision, decisions));
        MetadataReviewWorkspace workspace = Workspace(
            reviewed, persistenceAvailable: true, null, deferredQueryCount);
        try
        {
            await decisionStore.WriteAsync(
                workspace.LibraryRoot, CurrentDecisions(workspace), cancellationToken).ConfigureAwait(false);
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
            deferredQueries);
    }

    public async Task<MetadataReviewWorkspace> SetApplyAsync(
        MetadataReviewWorkspace workspace,
        MetadataReviewSubjectId subjectId,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        int index = workspace.Subjects.Select((value, position) => (value, position))
            .SingleOrDefault(value => value.value.Subject.Id == subjectId).position;
        if (workspace.Subjects.Count == 0 || workspace.Subjects[index].Subject.Id != subjectId)
            throw new ArgumentException("The metadata review subject is not current.", nameof(subjectId));
        ReviewedMetadataSubject current = workspace.Subjects[index];
        if (current.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Unavailable
            || current.IsAlreadyApplied)
            return workspace;
        MetadataReviewDecision? decision = MetadataReviewDecisionPolicy.CreateOverride(
            current.Subject, workspace.GenerationId, workspace.Revision, apply, DefaultApply(current));
        ReviewedMetadataSubject updated = decision is null
            ? new(current.Subject, DefaultApply(current), false, null, requiresReview: current.RequiresReview)
            : new(current.Subject, decision.Apply, true, decision.Key, requiresReview: current.RequiresReview);
        ReviewedMetadataSubject[] reviewed = workspace.Subjects.ToArray();
        reviewed[index] = updated;
        MetadataReviewWorkspace next = new(
            workspace.LibraryRoot,
            workspace.GenerationId,
            workspace.Revision,
            reviewed,
            workspace.PersistenceAvailable,
            workspace.PersistenceProblemCode);
        try
        {
            await decisionStore.WriteAsync(
                next.LibraryRoot, CurrentDecisions(next), cancellationToken).ConfigureAwait(false);
            return new(
                next.LibraryRoot,
                next.GenerationId,
                next.Revision,
                next.Subjects,
                persistenceAvailable: true);
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
                next.DeferredQueryCount);
        }
    }

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
            workspace.DeferredQueryCount);
        try
        {
            await decisionStore.WriteAsync(
                next.LibraryRoot, CurrentDecisions(next), cancellationToken).ConfigureAwait(false);
            return next;
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
                next.DeferredQueryCount);
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
            workspace.DeferredQueryCount);
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
