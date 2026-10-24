using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record FinalAuthorCatalogReconciliationResult(
    LibraryState? State,
    int ReconciledRecordCount,
    string? FailureCode = null,
    string? Explanation = null)
{
    public bool IsSuccess => State is not null && FailureCode is null;
}

public interface IFinalAuthorCatalogReconciler
{
    Task<FinalAuthorCatalogReconciliationResult> ExecuteAsync(
        LibraryState expected,
        CancellationToken cancellationToken);
}

public sealed class ReconcileFinalAuthorCatalogUseCase(
    ILibraryPathResolver pathResolver,
    ICalibreMetadataReader metadataReader,
    ILibraryStateSession libraryState,
    IClock clock) : IFinalAuthorCatalogReconciler
{
    public async Task<FinalAuthorCatalogReconciliationResult> ExecuteAsync(
        LibraryState expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.IsAuthoritative
            || !expected.IsWorkflowCheckpointCurrent
            || expected.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.Completed)
            return Failure("FINAL_AUTHOR_RECONCILIATION.STATE_INVALID",
                "Final author reconciliation requires current completed authoritative state.");

        LibraryValidationOutcome validation = await pathResolver.ValidateAsync(
            expected.Snapshot.Identity.LibraryRoot,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess)
            return Failure("FINAL_AUTHOR_RECONCILIATION.LIBRARY_INVALID",
                "The source library could not be validated for read-only author reconciliation.");
        CalibreCatalogReadOutcome read = await metadataReader.ReadAsync(
            validation.Location!,
            null,
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Failure("FINAL_AUTHOR_RECONCILIATION.CATALOG_UNAVAILABLE",
                "The source catalog could not be read after final author mutation.");

        CalibreCatalogRecord catalog = read.Catalog!;
        if (!string.Equals(
                catalog.LibraryUuid,
                expected.Snapshot.Identity.CalibreLibraryUuid,
                StringComparison.Ordinal)
            || catalog.SchemaVersion != expected.Snapshot.Identity.SchemaVersion)
            return Failure("FINAL_AUTHOR_RECONCILIATION.IDENTITY_CHANGED",
                "The source catalog identity changed after final author mutation.");
        Dictionary<long, CalibreBookRecord> live = catalog.Books.ToDictionary(value => value.Id);
        Dictionary<long, CalibreBook> projected = expected.Snapshot.Books.ToDictionary(value => value.Id.Value);
        if (!live.Keys.ToHashSet().SetEquals(projected.Keys))
            return Failure("FINAL_AUTHOR_RECONCILIATION.RECORDS_CHANGED",
                "The source record set changed after final author mutation.");

        List<(CalibreBook Book, CalibreBookRecord Live)> changed = [];
        foreach (CalibreBook book in expected.Snapshot.Books.OrderBy(value => value.Id.Value))
        {
            CalibreBookRecord current = live[book.Id.Value];
            if (!UnchangedNonAuthorState(book, current))
                return Failure("FINAL_AUTHOR_RECONCILIATION.NON_AUTHOR_STATE_CHANGED",
                    "Non-author source state changed after final author mutation.");
            if (!string.Equals(book.AuthorSort, current.AuthorSort, StringComparison.Ordinal)
                || !book.Authors.Select(AuthorIdentity).SequenceEqual(
                    current.Authors.Select(AuthorIdentity)))
                changed.Add((book, current));
        }
        if (changed.Count == 0) return new(expected, 0);

        LibraryStateRevision revision = expected.Revision;
        DateTimeOffset appliedAt = clock.GetUtcNow().ToUniversalTime();
        if (appliedAt < expected.ProjectedAtUtc) appliedAt = expected.ProjectedAtUtc;
        List<LibraryStateDelta> deltas = new(changed.Count);
        foreach ((CalibreBook book, CalibreBookRecord current) in changed)
        {
            deltas.Add(new ReconcileAuthorMetadataLibraryStateDelta(
                expected.GenerationId,
                revision,
                $"final-author-reconciliation:{book.Id.Value}",
                appliedAt,
                book.Id,
                current.AuthorSort,
                current.Authors.Select(value => new BookAuthor(
                    new(value.Id),
                    value.Name,
                    value.SortName))));
            revision = revision.Next();
        }
        LibraryStateSessionOutcome applied = await libraryState.ApplyBatchAsync(
            expected.Snapshot.Identity.LibraryRoot,
            deltas,
            CancellationToken.None).ConfigureAwait(false);
        if (!applied.IsSuccess)
            return Failure("FINAL_AUTHOR_RECONCILIATION.PROJECTION_FAILED",
                applied.Explanation ?? "Reconciled author state could not be projected.");
        LibraryState projectedState = applied.State!;
        LibraryStateSessionOutcome advanced = await libraryState.AdvanceWorkflowAsync(
            expected.Snapshot.Identity.LibraryRoot,
            LibraryWorkflowPhase.Completed,
            projectedState.ProjectedAtUtc,
            CancellationToken.None).ConfigureAwait(false);
        if (!advanced.IsSuccess)
            return Failure("FINAL_AUTHOR_RECONCILIATION.CHECKPOINT_FAILED",
                advanced.Explanation ?? "Reconciled author workflow state could not be published.");
        LibraryStateSessionOutcome checkpoint = await libraryState.CheckpointAsync(
            expected.Snapshot.Identity.LibraryRoot,
            CancellationToken.None).ConfigureAwait(false);
        if (!checkpoint.IsSuccess)
            return Failure("FINAL_AUTHOR_RECONCILIATION.CHECKPOINT_FAILED",
                checkpoint.Explanation ?? "Reconciled author state could not be checkpointed.");
        return new(advanced.State, changed.Count);

        FinalAuthorCatalogReconciliationResult Failure(string code, string explanation) =>
            new(null, 0, code, explanation);
    }

    private static bool UnchangedNonAuthorState(CalibreBook projected, CalibreBookRecord live) =>
        string.Equals(projected.Title, live.Title, StringComparison.Ordinal)
        && string.Equals(projected.RelativeDirectory, live.RelativeDirectory, StringComparison.Ordinal)
        && projected.Formats.Select(value => value.Format).ToHashSet(StringComparer.Ordinal)
            .SetEquals(live.Formats.Select(value => value.Format.ToUpperInvariant()))
        && projected.Identifiers.Select(value => (value.Type, value.Value)).Order().SequenceEqual(
            live.Identifiers.Select(value => (value.Type, value.Value)).Order());

    private static (long? Id, string Name, string SortName) AuthorIdentity(BookAuthor value) =>
        (value.Id?.Value, value.Name, value.SortName);

    private static (long? Id, string Name, string SortName) AuthorIdentity(CalibreAuthorRecord value) =>
        (value.Id, value.Name, value.SortName);
}
