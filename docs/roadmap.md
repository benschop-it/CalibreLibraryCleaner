# Roadmap

The historical milestone plans are archived under `docs/archive/`. This roadmap is
the current prioritized product direction.

## Completed baseline

### Foundation and read-only analysis

- .NET 10 WPF/Clean Architecture solution, DI, structured logging, tests, and ADRs.
- Read-only Calibre catalog/path loading and streaming SHA-256.
- Exact binary and exact normalized metadata evidence.
- EPUB assessment and isolated bounded PDF assessment.
- Persisted library state, assessment reuse, EPUB signature cache, and visible
  progress.

### Staged cleanup

- Exact-only Scan and Exact keeper review/cleanup.
- Read-only post-Exact residual reconciliation.
- Bounded deterministic Expanded discovery with candidate-only EPUB content.
- Disjoint Unified Candidate groups, evidence, generated keeper, override, and Skip.
- Fixed persistent `calibre-debug` worker for Exact and Candidate cleanup.
- External-backup confirmation, stop-on-ambiguity, and no automated recovery.
- Large-library safety, performance, progress, cache-reuse, and cleanup acceptance.

### Simplification already completed

- Removed general cleanup plans/execution history.
- Removed application-created backup bundles.
- Removed rollback/recovery UI and mutation fallback.
- Removed standalone Metadata/Expanded mutation and `Cleanup all`.
- Retained Metadata/Expanded as read-only evidence views.

## Priority 1: Measure and improve matching

1. Build and version a labeled same-work/language corpus with hard negatives,
   editions, translations, damaged metadata, and format diversity.
2. Establish candidate-generation recall and final group precision/recall baselines.
3. Add explainable false-positive/false-negative reports and threshold calibration.
4. Improve normalization/evidence only when holdout metrics improve; avoid
   library-specific aliases.
5. Measure keeper correctness separately from group correctness.

## Priority 2: Fast incremental analysis and caches

1. Add versioned SHA-256 reuse for unchanged stable file identity.
2. Add selective/periodic byte validation and a forced verification scan.
3. Unify cache identity/provenance across hashes, assessments, signatures,
   enrichment, embeddings, and derived evidence.
4. Invalidate/recompute affected records and candidate neighborhoods instead of the
   whole residual library where practical.
5. Track cold/warm duration, bytes read, cache hits, memory, parser work, and log
   volume as regression baselines.

## Priority 3: Richer local matching evidence

1. Add PDF cross-document fingerprints with explicit all-page versus sampled
   coverage.
2. Add calibrated content-language detection.
3. Add cover/visual and richer ebook structural comparison where labeled metrics
   justify it.
4. Improve cross-format evidence without retaining prose or claiming unsupported
   equivalence.

## Priority 4: Bibliographic and local-model evidence

1. Add provider-neutral online bibliographic ports and provenance values.
2. Enable configured providers by default with field disclosure, bounded requests,
   rate limits, caches, and local fallback.
3. Evaluate local embeddings/models for title, author, language, metadata, and
   bounded content similarity.
4. Version model inputs/outputs and benchmark value against deterministic evidence.
5. Calibrate evidence fusion; provider/model output remains advisory.

## Priority 5: Review experience

- Cover preview/comparison and richer side-by-side evidence.
- Fast filtering/sorting for large Candidate sets.
- Better explanation of edition/revision/illustration uncertainty.
- Metadata/series normalization suggestions.
- Efficient bulk Skip/keeper review without requiring review of every group.

## Priority 6: Simplify mutation state

After matching/cache work is stable:

- replace detailed projected delta/journal/checkpoint machinery with minimal durable
  workflow/run status where safe;
- retain one worker, deterministic operation ordering, backup confirmation,
  stop-on-ambiguity, and explicit Rescan before another mutation; and
- delete complexity rather than adding rollback/recovery features.

## Optional later work

- Multi-library comparison.
- Cross-platform UI evaluation with a separate process/filesystem decision.
- Plugin boundaries only if they cannot bypass matching provenance or worker-only
  mutation.
- Cloud AI only after a new privacy/authority decision; it is not currently accepted.

## Explicitly not planned

- Application-managed undo, rollback, or recovery.
- Application-created backup bundles.
- Guaranteed arbitrary cancellation or resumable partial analysis.
- Direct SQLite or Calibre-managed filesystem mutation.
- A second mutation engine or command fallback.
- Reintroducing general cleanup plans or category-specific cleanup commands.
