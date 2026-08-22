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
- A metadata operation rejected before any state change may be logged and skipped only
  when the worker proves the complete target field, cover, managed path, author sort,
  and protected local fields exactly match their pre-write state. Any changed or
  unreadable post-state remains ambiguous and stops the run.

## Implemented workflow

### 1. Exact analysis

An explicit Scan:

- validates the selected library and opens `metadata.db` read-only;
- loads records, authors, identifiers, publication metadata, languages, formats, and
  managed paths;
- resolves each declared format path and records missing/invalid associations;
- safely reuses SHA-256 for an unchanged stable file observation or streams a fresh
  digest with bounded concurrency;
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
  bounded shingle sketch;
- enabled-by-default, cache-first Open Library work identity for locally unresolved
  pairs, with deterministic title/author/language compatibility and field-category
  disclosure; it confirms same-work identity and does not fetch or apply corrected
  edition metadata;
- optional configured fixed-loopback Ollama metadata-embedding observations with
  immutable runtime/model identity, memory-only vectors, and reduced comparison
  cache; calibration produced no acceptable threshold, so these observations do not
  affect grouping or cleanup; and
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
versions. Reduced local embedding comparisons are cached by input hashes plus
runtime/model/preprocessing/comparison versions. Raw queries, model inputs, vectors,
and provider payloads are not stored. Cache loss affects performance, not matching
semantics.

SHA-256 cache entries use a one-way identity over canonical library root, expected
relative path, and hash policy; store only fingerprint, exact file observation, and
verification time; and are reused only after current path/reparse safety checks and
exact observation equality. Corruption or cache failure causes fresh hashing. Scan
requests can force full verification, and progress/logging separates reused from
fresh files and bytes.

## Progress and responsiveness

Long operations must keep WPF responsive and visibly active. Candidate preparation
reports phase-local bytes, files, pages, fingerprints, records/groups, active work,
and elapsed time. A heartbeat may update elapsed time but never invent completed
work. Presentation materialization reports exact item totals where available.

Cancellation is not required for new features and new cancellation points should not
be added by default. Existing Cancel behavior may remain best effort and stop only at
safe boundaries. Partial analysis is discarded rather than resumed; mutation cannot
be arbitrarily cancelled after it starts.

## Implemented rich-edition boundary

Provider-neutral edition queries, candidates, provider-specific proposals, and
selection reasons are implemented separately from same-work matching evidence. A
cache-first read-only use case can resolve every queryable retained record from each
provider independently. The Open Library adapter requests one nested relevance-ranked
edition per work and extracts bounded title, authors, validated ISBNs, publisher,
publication date, language, series, and cover reference. Google Books uses a bounded
partial-response volumes query and extracts title/subtitle, authors, validated ISBNs,
publisher, publication date, and language; cover fields are not requested in this
step. One provider failure does not block the other. A separate opaque-key cache
stores only normalized proposal fields and provenance, never raw responses, local
queries, or the Google key.

The WPF Online metadata settings dialog discloses that providers receive ISBN only,
or title, author names, and optional language. It accepts masked Save/Replace/Clear
input and stores only DPAPI ciphertext for the current Windows user outside the
repository and library. Plaintext is never reloaded for display.

Deterministic provider fusion produces High confidence only for cross-provider shared
ISBN agreement without conflicts; Medium for one exact ISBN or exact title+author
without conflicts; Low for weaker or conflicting evidence; and Unavailable when no
provider proposes an edition. High/Medium default selected and Low/Unavailable default
unselected. Cross-provider missing fields require a shared validated ISBN; identical
same-provider edition IDs may also complete a missing field. Provenance is retained
per provider and per resulting field.

After Candidate cleanup, subject construction creates one proposal target for every
retained record. Candidate preparation, Unified review, persisted load, and projected
state updates do not invoke edition providers. An explicit post-cleanup action runs
provider resolution/fusion and prepares review subjects. The WPF review
surface shows compact current metadata and a visually distinct proposed row with
Apply, confidence, reasons, provenance, all supported fields, and cover availability.
Filters are All, Applied automatically, Needs review, and Unavailable.

Only user Apply overrides are persisted. Each bounded decision is keyed by subject
ID, fusion policy version, primary provider/version/edition identity, generation, and
revision. Exact matches restore after restart; stale/incompatible decisions reset to
confidence defaults and are pruned. Keeper changes preserve compatible decisions.
The decision file contains no proposal metadata, provider payloads, API keys, or raw
library path. Candidate cleanup completes before online enrichment begins. A separate
confirmed metadata-only run sends current checked subjects through the fixed worker.
The worker merges live identifier maps, preserves and
verifies tags/ratings/comments/stored custom columns, verifies every field/cover
read-back, and projects Calibre's live managed path and author sort. Cover staging is
bounded, HTTPS-only, outside the library, and removed after the run.

## Accepted finish requirements

### Rich metadata proposals

- Create one metadata-review subject for every expected retained record: one subject
  per Unified group targeting its selected keeper, plus every residual singleton.
- Resolve rich edition candidates from Open Library and optional keyed Google Books
  independently, with bounded requests, complete provenance, versioned cache
  identity, and failure isolation.
- Persist each successful, NotFound, or transiently unavailable provider result as it
  completes. Reuse normal results for the configured cache lifetime and unavailable
  results for a short cooldown so interrupted review resumes without repeating failed
  requests immediately.
- When a provider request limit is reached, stop at the actual processed query count,
  report the uncached deferred count, and never count unrequested queries as completed.
- Choose one coherent best edition using validated ISBN, title/author/language,
  edition compatibility, provider agreement, completeness, and cover availability.
- Fill a missing field from another provider only when edition identity is proven.
- Propose supported title, authors, ISBN/identifiers, publisher, publication date,
  language, series/index, and cover without replacing local-only fields.
- Default High/Medium confidence proposals checked and Low proposals unchecked.

### Review and coordination

- Show current retained metadata and one visually distinct proposed row with Apply,
  confidence/reasons, provenance, supported fields, and cover indication/preview.
- Filter proposals by All, Applied automatically, Needs review, and Unavailable.
- Keep Metadata and Expanded tabs read-only; Unified review remains the only
  duplicate-cleanup selection surface.
- Prevent Scan, Load, Exact cleanup, Candidate preparation, Candidate cleanup, and
  final normalization from overlapping.
- Write every primary user-visible workflow status, progress/result summary, error,
  and recovery action verbatim to the normal bounded application log. Use stable event
  names so acceptance can retrieve Exact, Candidate, metadata-review, final counters,
  and controlled failure text without manual transcription.

### Approved mutation and release

- Send only checked supported metadata fields through a narrow typed operation on the
  fixed persistent worker, apply them through supported Calibre APIs before deleting
  source records, and verify read-back.
- During pre-mutation cover staging, retry bounded transient timeout, transport, HTTP,
  and refused-extra-redirect outcomes. If retries are exhausted, omit only that cover
  operation, report it separately, and keep other approved metadata eligible. Invalid
  trust chains, response type/content/bounds, or local staging remain blocking.
- Persist each successfully validated cover immediately in a bounded, versioned cache
  keyed by complete cover identity and validation-policy bounds. Revalidate cached
  bytes, dimensions, size, and SHA-256 before copying them into disposable worker
  staging. Corruption, loss, incompatibility, or cache unavailability becomes a miss;
  it never invents a cover or blocks an otherwise valid download. Report cache hits,
  downloads, and omissions during preparation.
- Stop on failed or ambiguous metadata mutation and require explicit Rescan.
- Store an optional Google Books key per Windows user with Windows protection; never
  log, export, cache, or redisplay its plaintext.
- Publish one deterministic versioned Windows x64 ZIP containing the WPF application,
  complete isolated PDF-worker publish, runtime dependencies, embedded Calibre worker,
  and a file size/SHA-256 manifest. Exclude symbols, tests, caches, credentials, logs,
  state, staging data, and source-machine paths. Package acceptance uses the executable
  directly and remains destructive only on an explicitly backed-up disposable library.
- After explicit approval, offer final Calibre-managed name normalization through
  supported Calibre behavior only. For author names with exactly one nonempty
  `Family| Given` separator, preview and convert display names to `Given Family` while
  preserving exact per-author and book sort values as `Family, Given`. Leave multi-
  separator, empty-side, or otherwise ambiguous names unchanged for explicit review.
  After metadata completion has converted legacy pipes to commas, normalize only books
  where every current author display equals its exact one-comma per-author sort. Keep
  mixed/nonmatching books unchanged.

## Explicit non-goals

- Preserving every edition as a separate executable group.
- Application-managed backup, undo, rollback, or recovery.
- Guaranteed arbitrary cancellation or restart of partial analysis.
- Direct SQLite writes or direct Calibre-managed filesystem mutation.
- Cloud AI as matching or mutation authority.
- Further duplicate-matching calibration, new matching providers, periodic
  maintenance, generalized cache work, synchronization, plugins, or multi-library
  comparison.
