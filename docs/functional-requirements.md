# Functional Requirements

This document separates behavior implemented today from accepted target work. Target
requirements do not describe current production behavior until implemented and
verified.

## Operating model

- The user selects one Calibre library and keeps Calibre/other writers closed while
  cleanup is running.
- Before each mutation run, the user confirms that a complete external backup exists.
- The application does not create, inspect, restore, or manage that backup.
- Analysis is read-only. It never writes directly to `metadata.db` or modifies
  Calibre-managed files.
- Mutation uses only the fixed persistent `calibre-debug` worker and supported
  Calibre APIs.
- A failed or ambiguous mutation stops. No retry or alternate mutation engine runs;
  explicit Rescan is required before another mutation.

## Implemented workflow

### 1. Exact analysis

An explicit Scan:

- validates the selected library and opens `metadata.db` read-only;
- loads records, authors, identifiers, publication metadata, languages, formats, and
  managed paths;
- resolves each declared format path and records missing/invalid associations;
- streams SHA-256 over every resolvable format with bounded concurrency;
- builds byte-identical format groups;
- excludes ambiguous exact title/author buckets with decisive known language or
  validated strong-identifier conflicts; and
- publishes an authoritative `ExactReady` generation.

Exact analysis does not run EPUB/PDF assessment or Candidate matching.

### 2. Exact cleanup

The user reviews the generated keeper for each byte-identical same-format group and
may override it or Skip the group. Exact cleanup:

- requires external-backup confirmation even for a NothingToDo run;
- transfers unambiguous complementary formats to one keeper;
- removes duplicate/transferred source formats;
- removes records only when they become empty;
- leaves conflicting or ambiguous records unchanged; and
- advances to `CandidatePreparationReady` after Completed or NothingToDo.

### 3. Candidate preparation

The first Candidate action is read-only. It:

- rereads and reconciles the residual catalog against the completed Exact outcome;
- reuses explained fingerprints and target-hashes transfer destinations when needed;
- preserves unchanged invalid paths as non-executable findings;
- reuses compatible EPUB/PDF assessments and cached EPUB signatures;
- detects exact normalized metadata candidates;
- runs bounded Expanded matching;
- merges evidence into disjoint same-work/language Candidate groups; and
- publishes `CandidateAnalysisReady` without starting a mutation worker.

Unexplained catalog/path/file changes reject preparation and require a new Exact
analysis. Incomplete preparation is not published.

### 4. Candidate review and cleanup

A Candidate group represents records believed to contain the same work and language.
Different editions, revisions, illustrations, or formatting may occur in one group.

The review UI shows evidence, contradictions, formats, assessment facts, generated
keeper, advisory classification, keeper override, Skip, and safe viewer opening.
Metadata and Expanded tabs remain read-only evidence views; Unified Candidate groups
are the executable surface. The evidence tabs may filter, navigate, select, and open
present formats in Calibre's viewer, but they do not own keeper/Skip choices or a
mutation command.

All published groups start included. `Cleanup eligible` and `To be reviewed` are
advisory; a group is processed unless the user selects Skip. Candidate cleanup:

- requires a second external-backup confirmation;
- transfers quality-ranked complementary formats absent from the keeper;
- keeps the keeper's existing same-format file;
- removes all formats from non-keepers; and
- removes non-keeper records proven empty.

## Implemented matching evidence

- exact file size plus SHA-256;
- exact normalized title and complete author set;
- normalized author identities supporting punctuation, comma order, initials, and
  non-conflicting full-name expansion;
- validated and embedded strong identifiers;
- title-token, series/index, language, edition-marker, and binary evidence;
- explicit author/language/series/identifier/content contradictions;
- candidate-only EPUB signatures containing 12 bounded token landmarks and a
  bounded shingle sketch; and
- enabled-by-default, cache-first Open Library work identity for locally unresolved
  pairs, with deterministic title/author/language compatibility and field-category
  disclosure; and
- deterministic component construction without blind weak-edge transitive closure.

Candidate generation uses indexes, per-record caps, and a global pair ceiling rather
than all-pairs comparison. Signatures contain hashes/counts only and retain no prose.

## Assessment and keeper evidence

EPUB assessment reports readability, package/navigation/spine/resource integrity,
metadata, identifiers, language, cover, text/chapter structure, encryption, bounded
fallback readability, explainable score/findings, and analyzer/scoring versions.

PDF assessment runs in a fresh bounded worker per file and reports strict open,
encryption, page/sampling, metadata, text/image, outline, inert active-content,
identifier, repeated/blank-page, resource, classification, score, and version facts.
It does not render, OCR, follow links, access the network, extract attachments, or
retain page text.

Keeper ranking uses completed compatible assessments, format coverage, metadata
quality, validated identifiers, cover evidence, and deterministic record-ID ties.
Scores are evidence for retention, not proof that two records match.

## Persistence and caches

Implemented persistence stores authoritative workflow state outside the library,
including generations/checkpoints and current projected mutation state. Startup
lists small manifests; explicit Load restores saved state without scanning.

Implemented caches reuse compatible EPUB/PDF assessments and EPUB content signatures
by fingerprint and analyzer/model/resource versions. Reduced Open Library work
resolutions are cached by a one-way query identity plus provider/query/resolution
versions; raw queries and provider payloads are not stored. Cache loss affects
performance, not matching semantics.

## Progress and responsiveness

Long operations must keep WPF responsive and visibly active. Candidate preparation
reports phase-local bytes, files, pages, fingerprints, records/groups, active work,
and elapsed time. A heartbeat may update elapsed time but never invent completed
work. Presentation materialization reports exact item totals where available.

Cancellation is not required for new features and new cancellation points should not
be added by default. Existing Cancel behavior may remain best effort and stop only at
safe boundaries. Partial analysis is discarded rather than resumed; mutation cannot
be arbitrarily cancelled after it starts.

## Target requirements

### Smarter matching

- Add PDF cross-document fingerprints with explicit all-page/sampled coverage.
- Add cover/visual and richer structural evidence where it improves labeled-corpus
  precision or recall.
- Add calibrated content-language detection.
- Add additional configured online bibliographic providers only where they improve
  labeled quality beyond the implemented Open Library source. Preserve provider,
  request-field, response-identity, retrieval-time, cache, and policy provenance.
- Add versioned local ML/embeddings for title, author, language, metadata, and bounded
  content similarity where benchmarks show value.
- Calibrate evidence fusion, contradictions, thresholds, and confidence on labeled
  train/holdout corpora. Do not hard-code library-specific aliases.
- Recompute only affected candidate neighborhoods where practical.

### Faster repeated analysis

- Persist and reuse SHA-256 when canonical path, size, timestamps, attributes/file
  identity, and cache provenance are unchanged.
- Selectively or periodically rehash cached files to detect drift.
- Offer a verification scan that forces full byte hashing.
- Version/invalidate every hash, assessment, signature, enrichment, embedding, and
  grouping cache by all relevant inputs and policies.
- Measure and expose cold/warm cache hit rates, durations, memory, and work counts.

### Simpler mutation state

- Replace detailed projected-delta/journal machinery with minimal durable workflow
  and run status where this can be done without allowing accidental continuation.
- Keep stop-on-ambiguity and explicit-Rescan-before-next-mutation behavior.
- Do not add rollback, recovery, backup bundles, execution history, or alternate
  mutation engines.

## Explicit non-goals

- Preserving every edition as a separate executable group.
- Application-managed backup, undo, rollback, or recovery.
- Guaranteed arbitrary cancellation or restart of partial analysis.
- Direct SQLite writes or direct Calibre-managed filesystem mutation.
- Cloud AI as matching or mutation authority.
