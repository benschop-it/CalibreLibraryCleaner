# Product Vision

## Product

Calibre Library Cleaner is a local-first Windows application that finds records for
the same work, explains the evidence, selects a practical keeper, and consolidates a
large Calibre library through supported Calibre tooling.

The product is designed for users who maintain a complete external backup of their
library. Cleanup is therefore optimized for useful matching and high throughput,
not for application-managed rollback or recovery.

## Primary goal

Make duplicate discovery as smart as possible while keeping repeated analysis fast.

An executable Candidate group means **the same work and language**. It may include
different editions, revisions, illustrations, or formatting. The application makes
that uncertainty visible, proposes a keeper, and lets the user change the keeper or
Skip the group. Published groups are processed unless skipped.

## Priorities

1. Matching precision and recall.
2. Cold and warm performance, incremental caches, and bounded resource use.
3. Explainable evidence and efficient keeper/Skip review.
4. Simple, reliable mutation through one supported Calibre worker.
5. Progress visibility so long operations never look hung.

Cancellation, resumable partial analysis, automated rollback, and automated recovery
are not primary product goals.

## Operating assumptions

- The user confirms a complete external backup before every mutation run.
- Calibre and other library writers remain closed while the cleaner mutates a
  library.
- Analysis never writes directly to `metadata.db` or Calibre-managed files.
- Mutation uses only the fixed persistent `calibre-debug` worker.
- A failed or ambiguous mutation stops; the user restores externally if necessary
  and runs an explicit Rescan before further cleanup.

## Evidence strategy

Matching grows by combining independent, versioned evidence:

- local metadata, authors, identifiers, series, language, and binary fingerprints;
- EPUB and PDF content/structure evidence;
- cover and visual evidence where useful;
- configured online bibliographic providers, enabled by default and recorded with
  provenance; and
- local ML/embedding evidence for difficult title, author, language, and content
  variants.

No single weak signal silently determines a group. Evidence and contradictions stay
visible, and external/provider failure falls back to local matching.

## Performance strategy

- Stage Exact cleanup before expensive residual matching.
- Cache hashes, assessments, signatures, enrichment, and model outputs by stable
  input identity plus algorithm/model/resource versions.
- Reuse unchanged SHA-256 values, with selective or periodic byte validation.
- Recompute affected records and candidate neighborhoods instead of the complete
  library where practical.
- Bound concurrency and parser resources.
- Measure cold/warm durations, cache hit rates, memory, candidate counts, and
  mutation throughput on disposable large libraries.

## Implemented baseline

- Exact-only scan and exact binary grouping.
- Staged Exact cleanup followed by trusted Candidate preparation.
- Local deterministic same-work/language discovery using metadata, identifiers,
  series/language constraints, exact binary relations, and candidate-only EPUB
  content signatures.
- EPUB and isolated PDF quality assessment.
- Unified Candidate groups with evidence, generated keeper, keeper override, and
  Skip.
- Persistent assessment/signature caches and measured progress.
- Fixed persistent Calibre mutation worker for Exact and Candidate cleanup.
- External-backup confirmation and no automated recovery.

## Success measures

- Better labeled-corpus precision and recall than the current matcher.
- More true same-work records found without unbounded pair comparison.
- Warm scans and Candidate preparation reuse most unchanged work.
- Progress remains visibly active throughout long phases.
- Tens-of-thousands-record libraries complete within measured, improving resource
  budgets.
- Cleanup remains explainable and uses no direct database or managed-file writes.
