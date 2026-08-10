using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ILibraryStateStore
{
    Task<IReadOnlyList<PersistedLibraryStateInfo>> ListAsync(CancellationToken cancellationToken);

    Task WriteBaselineAsync(LibraryState state, CancellationToken cancellationToken);

    Task WritePostExactRefreshBaselineAsync(
        LibraryState state,
        LibraryWorkflowSource source,
        CancellationToken cancellationToken);

    Task WriteCandidateAnalysisAsync(
        string libraryRoot,
        LibraryState state,
        LibraryWorkflowSource source,
        CancellationToken cancellationToken);

    Task AppendDeltaAsync(
        string libraryRoot,
        LibraryStateDelta delta,
        LibraryState projectedState,
        CancellationToken cancellationToken);

    Task AppendDeltaBatchAsync(
        string libraryRoot,
        IReadOnlyList<LibraryStateDelta> deltas,
        LibraryState projectedState,
        bool compactIfThresholdReached,
        string? mutationIntentId,
        bool completeMutationIntent,
        CancellationToken cancellationToken);

    Task WriteMutationIntentAsync(
        string libraryRoot,
        LibraryStateMutationIntent intent,
        CancellationToken cancellationToken);

    Task WriteWorkflowCheckpointAsync(
        string libraryRoot,
        LibraryState state,
        CancellationToken cancellationToken);

    Task CompactAsync(
        string libraryRoot,
        LibraryState state,
        CancellationToken cancellationToken);

    Task WriteUncertaintyAsync(
        string libraryRoot,
        LibraryState state,
        CancellationToken cancellationToken);

    Task<LibraryState?> ReadAsync(string libraryRoot, CancellationToken cancellationToken);

    Task<PostExactRefreshBasis?> ReadPostExactRefreshBasisAsync(
        string libraryRoot,
        CancellationToken cancellationToken);

    Task<LibrarySnapshot?> ReadReusableAssessmentSnapshotAsync(
        string libraryRoot,
        CancellationToken cancellationToken);
}

public sealed record PostExactRefreshBasis(
    LibraryState PreExactState,
    IReadOnlyList<LibraryStateDelta> CompletedExactDeltas);

public sealed record PersistedLibraryStateInfo(
    string LibraryRoot,
    DateTimeOffset ScannedAt,
    DateTimeOffset ProjectedAt,
    LibraryStateGenerationId GenerationId,
    LibraryStateRevision Revision,
    LibraryStateStatus Status);
