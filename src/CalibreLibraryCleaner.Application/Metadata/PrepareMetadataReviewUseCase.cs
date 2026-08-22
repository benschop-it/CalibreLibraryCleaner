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
            .Sum(value => Math.Max(0, value.QueryCount - value.CacheHits - value.ProviderRequests));
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
                MetadataReviewDecisionPolicy.Reconcile(subjects, state.GenerationId, state.Revision, []),
                persistenceAvailable: false,
                "METADATA_REVIEW.DECISION_READ_FAILED",
                deferredQueryCount);
        }
        IReadOnlyList<ReviewedMetadataSubject> reviewed = MetadataReviewDecisionPolicy.Reconcile(
            subjects, state.GenerationId, state.Revision, decisions);
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
        if (current.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Unavailable)
            return workspace;
        MetadataReviewDecision? decision = MetadataReviewDecisionPolicy.CreateOverride(
            current.Subject, workspace.GenerationId, workspace.Revision, apply);
        ReviewedMetadataSubject updated = decision is null
            ? new(current.Subject, current.Subject.Proposal.IsSelectedByDefault, false, null)
            : new(current.Subject, decision.Apply, true, decision.Key);
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ReviewedMetadataSubject[] reviewed = workspace.Subjects.Select(current =>
        {
            if (current.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Unavailable)
                return new ReviewedMetadataSubject(current.Subject, false, false, null);
            MetadataReviewDecision? decision = MetadataReviewDecisionPolicy.CreateOverride(
                current.Subject, workspace.GenerationId, workspace.Revision, apply);
            return decision is null
                ? new ReviewedMetadataSubject(
                    current.Subject, current.Subject.Proposal.IsSelectedByDefault, false, null)
                : new ReviewedMetadataSubject(current.Subject, decision.Apply, true, decision.Key);
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
