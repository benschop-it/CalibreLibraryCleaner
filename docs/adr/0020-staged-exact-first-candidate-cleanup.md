# ADR 0020: Stage Exact Cleanup Before Unified Candidate Cleanup

- Status: Accepted, amended by ADR 0021
- Date: 2026-08-10
- Amends: ADR 0012, ADR 0017, ADR 0018, ADR 0019
- Supersedes: the one-scan composite cleanup workflow for future development

> Implementation status: staged mode is the production WPF configuration. Shadow
> parity and large-library acceptance passed; `Cleanup all` and standalone
> Metadata/Expanded mutation surfaces are retired. Their read-only evidence views
> remain. ADR 0021 amends mandatory full rehashing as a future target, not current
> implementation.

## Context

The current workflow performs catalog loading, full hashing, EPUB/PDF assessment,
exact and metadata grouping, Expanded discovery, and recommendation generation in
one scan. Users then review three cleanup categories and may execute one composite
cleanup graph. On large libraries, expensive candidate analysis is performed for
files that exact cleanup may subsequently remove.

Exact binary cleanup already has accepted deterministic retention, transfer,
removal, worker, projection, checkpoint, and failure behavior. Replacing or
refactoring that algorithm would add risk without helping stage the expensive work.

## Decision

Adopt a staged workflow for future development:

1. run an exact-only analysis that reads the catalog, resolves safe paths, hashes
   every current format with streaming SHA-256, and publishes Exact groups;
2. run the existing Exact cleanup algorithm unchanged;
3. after successful Exact cleanup, including a successful nothing-to-do result,
   reconcile a fresh read-only catalog against authoritative projected state and
   completed typed deltas;
4. reuse fingerprints and compatible assessments only when that reconciliation
   proves their provenance, and fail closed on unexplained changes;
5. discover one disjoint set of residual candidates from exact metadata and
   Expanded evidence; and
6. review and execute one Candidate cleanup through the fixed persistent worker.

A durable, versioned workflow checkpoint is bound to the canonical library root,
authoritative generation and revision, and relevant policy versions. Conservative
migration maps state without a compatible checkpoint to `RequiresExactAnalysis`.
Candidate preparation is unavailable until Exact cleanup succeeds for the bound
generation. Uncertain state overrides the checkpoint and blocks mutation until a
new exact analysis succeeds.

During migration, the legacy full scan, category cleanup commands, and `Cleanup
all` may remain runnable until the staged replacement reaches behavioral and safety
parity. One authoritative generation must never be mutable through both workflow
architectures. Retirement of legacy surfaces is a later, dedicated slice.

The Exact cleanup implementation is a fixed dependency of this decision. Its
binary grouping, retention ranking, transfer/removal planning, worker execution,
typed deltas, checkpointing, and failure behavior remain unchanged. Staged
orchestration may gate and record the existing use case but must not alter its
algorithm.

## Consequences

- Initial exact analysis still hashes every current format but performs no ebook
  assessment, recommendation generation, metadata grouping, or Expanded discovery.
- Candidate work runs only on the residual post-exact library.
- Candidate preparation rereads the catalog without becoming an implicit full scan.
- Fingerprint reuse requires authoritative pre-exact identity and an explained
  unchanged or transferred association; timestamps and size are never sufficient.
- Exact-metadata-only candidates remain present as advisory `To be reviewed`
  candidates even without EPUB content evidence.
- Exact and Candidate cleanup are separate mutation runs and require separate
  external-backup confirmations.
- The one-scan composite design remains implemented only as transitional behavior
  until parity permits its explicit retirement.

## Rejected alternatives

- Change the Exact cleanup algorithm while introducing staging.
- Skip SHA-256 during initial exact analysis.
- Trust timestamps, size, paths, or record IDs as post-cleanup file identity.
- Trigger a normal full scan from cleanup or candidate preparation.
- Prepare candidates and mutate them in the same user activation.
- Keep Metadata and Expanded as separate long-term mutation authorities.
- Remove legacy mutation surfaces before staged parity is proven.
- Add another mutation engine or direct-command fallback.