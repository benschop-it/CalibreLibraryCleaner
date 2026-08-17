# Architecture

This document names both the implemented architecture and accepted target changes.
Target sections are not claims about current production code.

## Projects and dependency direction

```text
Domain <- Application <- Infrastructure
                    <- Wpf

PdfWorker (standalone executable, provider-specific parsing)
```

- **Domain:** immutable library, assessment, matching, evidence, group, keeper, and
  state values plus deterministic policies.
- **Application:** use cases, orchestration, progress contracts, and integration
  ports.
- **Infrastructure:** read-only SQLite, filesystem/path identity, hashing, ebook
  inspection, caches/state persistence, Calibre discovery/worker process, and JSON.
- **WPF:** selection, progress, evidence review, keeper/Skip choices, and composition.
- **PdfWorker:** one isolated bounded PDF parse per process.

Domain has no integration dependencies. Application depends only on Domain. WPF may
reference Infrastructure only in `App.xaml.cs`.

## Implemented staged pipeline

```text
Explicit Exact Scan
  -> catalog/path resolution
  -> safe SHA-256 identity reuse or full hashing
  -> Exact groups
  -> Exact review and worker cleanup
  -> read-only residual reconciliation
  -> assessment/signature reuse and misses
  -> exact-metadata + Expanded discovery
  -> disjoint Unified Candidate groups
  -> Candidate review and worker cleanup
```

Exact and Candidate mutation are separate runs with separate external-backup
confirmation. Candidate preparation is a read-only activation; Candidate execution
is a later activation.

## Matching architecture

### Implemented

Domain builds bounded `BookMatchingProfile` values and candidate pairs from author
identities and independent work evidence. Inverted indexes avoid all-pairs search.
Ordinary candidates are capped per record and globally. Decisions retain positive
evidence and contradictions.

Ambiguous pairs with two usable EPUBs request hash-only content signatures.
Application deduplicates fingerprint demand, applies bounded concurrency, and uses a
versioned Infrastructure cache. Domain compares signatures symmetrically and forms
components only through compatible anchor/strong relations. Complete-component
language, author, series, identifier, edition-marker, and content contradictions
block unsafe unions. Final groups are partitioned by normalized language.

Locally unresolved ambiguous/unavailable-content pairs may use bounded Open Library
work search. Application selects and caps queries, reads/writes reduced resolution
cache entries, reports progress, and applies Domain resolution/fusion. Infrastructure
owns fixed-endpoint HTTPS, rate limiting, response parsing, and atomic cache files.
Only identical resolved provider work IDs add positive evidence; ambiguity, mismatch,
failure, timeout, or cache loss remains neutral and local matching continues.

Exact normalized metadata evidence is mandatory input to unified grouping and is not
suppressed by ordinary candidate caps. The unified merge policy produces disjoint
executable groups; each record occurs in at most one group.

### Target

Add independent provider-neutral evidence boundaries for:

- PDF whole-document/sampled fingerprints;
- cover and visual similarity;
- content-language classification;
- additional online bibliographic work/edition identity; and
- local embedding/model outputs.

Every evidence result carries input identity, provider/model/policy versions,
coverage, confidence, problems, and provenance. Evidence fusion remains in Domain and
must be calibrated on labeled data. Online failure cannot block local matching.

## Cache architecture

### Implemented

- Authoritative library state and workflow manifests live outside the library.
- Compatible EPUB/PDF assessments are reusable by fingerprint and all analyzer,
  scoring, classification, and resource versions.
- EPUB signatures are cached by fingerprint and signature/inspection versions.
- SHA-256 fingerprints are cached by a one-way identity over canonical library root,
  expected relative path, and hash policy. Reuse occurs only after current
  containment/reparse preflight and exact size/creation/last-write/attribute match.
  A forced-verification request bypasses reads and refreshes successful entries.
- Open Library resolutions are cached by query hash and all provider/query/resolution
  policy versions; entries contain reduced work identity evidence, not raw payloads.
- Cache entries contain technical facts/hashes, not prose or absolute library paths.
- Atomic writes and bounded pruning make cache loss a performance event, not a
  correctness event.

### Target

Introduce a common versioned cache identity across assessments, signatures,
enrichment, embeddings, and derived matching artifacts. Selectively/periodically
revalidate cached SHA-256 bytes, expose forced verification in the UI, and track
dependency edges so changed records invalidate only affected profiles, pairs,
evidence, and groups where practical.

## Analysis and progress

Analysis opens `metadata.db` read-only and managed files read-only. EPUB/PDF inputs
remain untrusted and are processed with bounded sizes, counts, concurrency, and
parser/process limits.

Long operations are asynchronous and non-blocking to WPF. Progress is mandatory and
uses truthful phase-local units plus elapsed heartbeat. Cancellation is optional and
best effort; an incomplete analysis is discarded and never published.

## Assessment boundaries

Application owns provider-neutral EPUB/PDF inspection requests, limits, results,
scoring, sampling, and orchestration. Infrastructure owns ZIP/XML/HTML/filesystem and
PdfPig/process details.

PDF parsing uses one fresh worker with a cleared environment, managed-heap limit,
Windows Job Object where available, CPU/working-set/wall-time watchdogs, and a
versioned bounded stdio protocol. The worker cannot render, OCR, execute actions,
follow links, use the network, or retain full text.

Infrastructure launches the fixed sibling PdfWorker executable for one file, sends a
versioned request and Application-selected page sample over bounded JSON stdio, reads
provider-neutral progress/results, and terminates/disposes the process. Per-file
process isolation contains parser/decoder crashes and resource pressure without
giving the worker mutation authority over Calibre.

## Mutation boundary

### Implemented

Application builds deterministic transfer, format-removal, and empty-record-removal
operations. Infrastructure opens one trusted persistent `calibre-debug` worker using
a fixed script and strict typed JSON-lines protocol. Chunks contain at most 100
operations. Transfers precede dependent removals; record removals are last.

Current persistence uses authoritative generations/revisions, workflow checkpoints,
a mutation marker, hash-chained typed delta journal, complete-chunk projection, and
uncertainty blocking. It is the implemented baseline, not the long-term complexity
target.

No direct SQLite write, shell, direct managed-file mutation, arbitrary script,
`calibredb` fallback, or second mutation engine is allowed.

### Target

Retain only enough durable workflow/run status to prevent accidental continuation.
After failed or ambiguous mutation, stop and require explicit Rescan. Do not add
rollback/recovery models, backup bundles, execution history, or reconciliation-heavy
state.

## Online bibliographic evidence

Open Library is enabled by default for low-volume user-triggered Candidate analysis.
Infrastructure owns fixed HTTPS, optional contact configuration, provider rate
limits, bounded response parsing, and provider cache files behind Application ports.
Application requests only unresolved records, caps a run at 24 cache misses, and
transmits validated ISBN when available or bounded title, author, and optional
language fields. Domain owns provider-neutral work resolution and evidence fusion.

The UI/settings disclose enabled providers and transmitted field categories. Logs do
not contain book metadata or provider payloads. Provider results are advisory,
versioned, cached, and reproducible from stored query hashes and reduced evidence.
Network/provider/cache failures degrade to local matching. Set
`CALIBRE_OPEN_LIBRARY_ENABLED=0` to disable the provider; optional
`CALIBRE_OPEN_LIBRARY_CONTACT` identifies regular requests without being logged.

A separate edition-metadata boundary is invoked only by an explicit action after
Candidate cleanup. Domain owns bounded provider-neutral edition queries, coherent
edition candidates, and deterministic one-provider proposal selection. Application
runs each configured provider independently through
cache-first orchestration, so one failure does not block another. The rate-limited
Open Library adapter requests one nested relevance-ranked edition per work. Google
Books uses the bounded public volumes endpoint only when a current-user DPAPI-
protected API key is configured; it requests title/subtitle, authors, publisher,
published date, industry identifiers, and language, but no cover fields in this
slice. A separate reduced cache stores normalized proposal fields and provenance
under opaque provider-specific query hashes and never receives the key. The settings
dialog displays only configured status and masked Save/Replace/Clear input.

Domain fusion now chooses one deterministic primary edition using validated ISBN,
title/author/language compatibility, provider support, publisher/date/series
compatibility, provider match score, and completeness. Cross-provider missing fields
are filled only for a shared validated ISBN; same-provider completion additionally
accepts the identical edition ID. Conflicting provider facts lower confidence. Every
fused result retains provider identities/versions, retrieval times, proposals, field
sources, reasons, and `edition-metadata-fusion/1.0.0` policy identity.

After Candidate cleanup, projected Unified groups are cleared and Application creates
one metadata-review subject for every retained record. Candidate preparation,
Candidate review, persisted load, and projected-state updates never invoke edition
providers. The user explicitly starts post-cleanup provider resolution; compatible
Apply overrides restore only for matching subject, policy, provider/version/edition,
generation, and revision identities.

Infrastructure persists only bounded Apply overrides in an atomic path-private file;
no proposal fields, local metadata, provider payloads, API keys, or library paths are
stored. WPF presents one recycling/virtualized subject grid with compact current
metadata and a distinct proposed row, confidence/reasons/provenance, Apply, four
filters, and current/proposed cover availability/reference text.

Candidate cleanup first transfers/removes reviewed duplicate records and advances to
a post-cleanup checkpoint. A separate confirmed metadata-only worker run accepts the
current compatible checked review subjects. Infrastructure stages trusted Open Library
covers through bounded HTTPS into an execution-scoped directory outside the library.
The fixed persistent worker merges identifiers from the live Cache, verifies stored
local fields and exact read-back, and returns the live managed path and author sort for
state projection. Provider/model evidence still has no direct mutation authority.

## Windows release assembly

Release packaging publishes the WPF application and PDF worker independently for
`win-x64`, framework-dependent on the .NET 10 Desktop Runtime. The complete PDF worker
publish remains under `pdf-worker/`, matching its default process path and preventing
same-named dependency collisions with the WPF publish. Release correctness does not
depend on the WPF project's development-only post-build copy target.

The deterministic package workflow merges only those two explicit publish outputs,
rejects symbols/tests/caches/logs/state/credentials/temporary files and source-machine
paths, emits a sorted SHA-256 manifest, and creates a ZIP with sorted entries and fixed
timestamps. The packaged WPF executable provides a no-UI `--release-smoke` path that
validates required files and the fixed embedded Calibre mutation worker without
initializing logging, state, providers, or child workers.

## Local ML

Infrastructure owns model files/runtime and bounded inference. Application owns
batching, cache keys, progress, and model-selection ports. Domain receives only
versioned vectors/similarity/classification evidence, never runtime tensors or model
objects. A model upgrade invalidates dependent evidence and groups.

Configured Ollama metadata embeddings are implemented as observational evidence.
The endpoint is fixed to `http://127.0.0.1:11434`; the application never starts
Ollama or downloads a model. Configuration requires runtime version, model name, and
immutable model digest. Only bounded title, author, and language text is embedded.
Vectors remain in memory; pair caches contain only input hashes, complete versions,
status, and cosine similarity permille. Observations do not affect grouping until a
separate threshold decision is calibrated and accepted. Expanded calibration found
positive and same-author negative overlap at 850 permille, so metadata embeddings
remain observational and no activation threshold is planned for this model/input.

## Logging and privacy

Production defaults to aggregate Information events plus Warning/Error. Detailed
matching/inspection diagnostics require explicit developer diagnostics and remain
bounded. Never log book content, titles, authors, identifiers, paths, provider
payloads, or embeddings by default.

## Current architectural debt

- Cached SHA-256 identities have forced verification but not periodic/selective byte
  revalidation or a dedicated UI command.
- Current mutation state is more detailed than the accepted minimal target.
- Full compatibility scan code remains for compatibility although staged mode is the
  configured product workflow.
- PDF matching, additional online providers, local ML, incremental candidate recomputation, and
  calibrated evidence fusion are not implemented.
