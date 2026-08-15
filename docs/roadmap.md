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

Completed measurement foundation:

- versioned labeled calibration/holdout corpus with hard negatives, editions,
  translations, damaged metadata, format diversity, and minimal CC0 sourced works;
- full Domain pipeline evaluator with pair/group/candidate/language/keeper metrics;
- opaque stage-specific false-positive/false-negative reports and cap diagnostics;
- contradiction-aware Exact Metadata removed all 20 measured false positives;
- edition-neutral title similarity recovered all 10 previously unproposed positives;
- enabled-by-default Open Library evidence recovered both sourced public-domain
   work families with bounded cache-first lookup and local fallback;
- reviewed semantic baseline: 100% precision, 95.6897% recall, 97.7974% F1,
   100% candidate-route recall, 95.2830% keeper coverage, and 100% keeper
  accuracy on scored groups.

Matching calibration is now frozen. The remaining ten synthetic ambiguous-content
cases are retained as known limitations rather than targets for more metadata
heuristics. New matching evidence may be evaluated later only as a genuine product
capability with independently expanded data.

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

Completed:

- provider-neutral work resolution, evidence provenance, and Domain fusion;
- enabled-by-default Open Library search with field disclosure, bounded requests,
  rate limiting, reduced cache, and local fallback.
- fixed-loopback Ollama metadata-embedding observations with complete runtime/model
   provenance and reduced comparison cache; final `embeddinggemma` calibration found
   positives at 850-932 and hard negatives at 647-850, so no threshold was activated.

Closed for now:

- metadata-only embedding calibration cannot preserve both perfect corpus precision
  and recall with one threshold;
- model output remains observational;
- future privacy-safe content models require a separate product plan and new data,
  not continued tuning of this corpus.

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
