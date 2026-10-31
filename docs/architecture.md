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

The read-only SQLite adapter decodes Calibre's comma-as-pipe author-name storage
before constructing catalog values. Display names match supported Cache API
spellings; author IDs, sort values and credit order remain exact. This is storage
translation, not an author correction, alias or fuzzy comparison. Old uncertain
snapshots still require explicit Scan; they are not silently reauthorized.

```text
Explicit Exact Scan
  -> catalog/path resolution
  -> safe SHA-256 identity reuse or full hashing
  -> Exact groups
  -> automatic no-group advancement, or Exact review and worker cleanup
  -> read-only residual reconciliation
  -> assessment/signature reuse and misses
  -> exact-metadata + Expanded discovery
  -> disjoint Unified Candidate groups
  -> Candidate review and worker cleanup
```

Exact and Candidate mutation are separate runs with separate external-backup
confirmation. Candidate preparation is a read-only activation; Candidate execution
is a later activation.

Expanded evidence summaries are presentation-state aware. An Exact-only snapshot
points to Candidate preparation; projected snapshots after completed Candidate
cleanup point to online metadata review instead of asking to repeat Scan. These
messages require the exact displayed snapshot to belong to a current authoritative
workflow checkpoint. Legacy/untrusted states and unexpectedly absent Candidate
analysis evidence retain rescan warnings. No obsolete groups or evidence are made
executable to avoid an unavailable summary.

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

EPUB front-matter extraction explicitly validates unique package manifest IDs
before following spine references. A duplicate, including an identical declaration
or conflicting media type, produces `EPUB_FRONT_MATTER.DUPLICATE_MANIFEST_ID`.
Application retains the code as that record's disposition evidence while continuing
other records. Duplicate normalized ZIP paths remain rejected by archive preflight;
distinct manifest IDs referencing the same resource are not treated as duplicate IDs.

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

Application builds deterministic transfer, format-removal, empty-record-removal, and
final quarantine copy/delete operations. Infrastructure opens one trusted persistent
`calibre-debug` worker using a fixed script and strict typed JSON-lines protocol.
Chunks contain at most 100 operations. Same-library transfers and cross-library
quarantine copies precede dependent removals; quarantine source deletions run as
one-record chunks only after every destination record has exact read-back.

The quarantine destination is the fixed physical sibling of the source. Infrastructure
inspects an existing destination read-only and binds its UUID to the review. The same
worker opens or creates it through Calibre APIs, creates missing custom columns only
for a new destination, uses duplicate record creation rather than merge/overwrite,
and reopens the destination cache after schema changes. Destination state is not a
second application-managed library state or synchronization boundary.

Calibre author names and sorts are shared entities, so one reviewed final-author write
can update other records that reference the same author. After the final-author worker
completes, Application performs one read-only catalog reconciliation. Exact library,
record, title, managed-path, identifier, and registered-format identity is required;
only ordered author IDs/names/sorts and book author-sort changes are projected. Any
non-author difference marks state uncertain and blocks quarantine until Rescan.

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
Open Library adapter inspects several bounded nested editions per work and Application
orchestrates strict-to-broader query variants with accurate request accounting. When
search identifies a compatible work but exposes no usable nested edition, a budget-
aware provider call may spend one additional counted request on the supported work
editions endpoint and inspect at most five editions. Google Books uses the bounded
public volumes endpoint only when a current-user DPAPI-
protected API key is configured; it requests title/subtitle, authors, publisher,
published date, industry identifiers, and language, but no cover fields in this
slice. Requests are serialized and paced at no more than one start every three seconds by
default to avoid burst rate limits. Each explicit preparation spends at most 200
Google Books requests by default, while Open Library retains its 10,000-request
high-coverage budget. Uncached deferred query groups are reported and reruns continue
from immediate cache writes. A separate reduced cache stores normalized
proposal fields and provenance
under opaque provider-specific query hashes and never receives the key. This cache is
global to the current Windows user, retains normal entries for 365 days, and prunes
oldest entries above 1 GiB. Successful candidates are additionally indexed by every
validated candidate ISBN, so applying provider metadata and rescanning does not turn
that new ISBN into an avoidable online lookup. Existing successful entries are
backfilled once per provider/query/alias policy. The settings dialog displays only
configured status and masked Save/Replace/Clear input.

Application performs one global cache prepass per provider before starting any HTTP
request. A group is completed in this pass only when its strict-to-broader variant
sequence is terminal or wholly cached; the first missing variant and any preceding
NotFound result are retained for the later online pass. This changes processing
order, not evidence precedence, provider budgets, results, or eventual online work.
Progress therefore exposes compatible cache reuse before cache-poor ISBN groups.

Provider-edition comparison uses literal author keys independently of local Candidate
pseudonym normalization. Explicit annotations are cleaned in outgoing query values,
so only affected input identities change. The 1.6 edition-query policy can reuse 1.4
negative results for identical inputs and the unchanged provider version; it does
not reuse older-provider extraction failures or uncertain 1.5 text misses. This
keeps ordinary cached misses warm without guessing across changed validation rules.

Domain fusion policy `edition-metadata-fusion/1.1.0` chooses one deterministic primary edition using validated ISBN,
title/author/language compatibility, provider support, publisher/date/series
compatibility, provider match score, completeness, and a bounded all-caps display
penalty. It prefers mixed-case candidates but never invents capitalization or removes
a sole valid all-caps candidate. Cross-provider missing fields
are filled only for a shared validated ISBN; same-provider completion additionally
accepts the identical edition ID. Conflicting provider facts lower confidence. Every
fused result retains provider identities/versions, retrieval times, proposals, field
sources, reasons, and the fusion policy identity.

Mutation orchestration places author metadata writes after every non-author operation
and sends each author write as a one-operation worker chunk. This preserves fail-closed
readback verification while allowing all unrelated work to checkpoint before a
Calibre author canonicalization mismatch can stop the run.

After Candidate cleanup, projected Unified groups are cleared and Application creates
one metadata-review subject for every retained record. Candidate preparation,
Candidate review, persisted load, and projected-state updates never invoke edition
providers. The user explicitly starts post-cleanup provider resolution; compatible
Apply overrides restore only for matching subject, policy, provider/version/edition,
generation, and revision identities.

Application reconciles confidence defaults against the current retained book. Only
additive or bounded casing-improvement merges remain automatic; destructive field
differences become unchecked Needs review rows. Explicit Apply is persisted relative
to that book-aware default. Domain owns one merge preview used by WPF and the same
applicable-field policy consumed by execution.

Provider author lists pass through the same author-write preparation in
applicability, preview, metadata execution and final bibliographic-author handoff.
Pseudonym-annotated provider lists are withheld as a complete field, including the
explicit `psued.` spelling. WPF displays prepared writable authors or a withholding
message, separately retaining raw credits as provider evidence. Fused review policy
`1.6.0` invalidates prior Apply overrides. Explicit comma/pipe family-given
notation uses the existing name normalization policy to produce given-first display
values, including bounded alternating pipe pairs. Prepared values—not raw
provider labels—drive preview, application, casing defaults and final handoff equality.
Provider candidate credits retain source order instead of being alphabetically sorted.
Proposal interpretation `1.1.0` preserves new source credit order. Recognized
`1.0.0` proposal/query identities remain reusable under exact input/version checks
and local positive revalidation, including compatible query `1.6.0` outcomes and
identifier-alias backfill. Compatible results are promoted to current keys without
renewing retrieval times or failure retry windows. Original provider/query/proposal
versions and opaque source query identity survive promotion, aliases and book
rebinding; WPF discloses the original versions separately from current validation.
Local positive promotion also seeds missing ISBN aliases after the one-time
backfill marker, without overwriting newer evidence or recursively expanding aliases.
Known older extraction misses, unsupported versions, stale or corrupt entries
remain misses. Existing artifacts and provider request budgets are preserved.

Legacy outer author arrays lost their original order. This coverage flag survives
cache round-trip and fusion. A single stored credit retains its internal order.
Unknown multi-credit order permits author preparation only when the complete
case-insensitively unique current credit set independently establishes exact
correspondence and order; otherwise the entire author field is withheld while
independently applicable metadata remains usable. Missing order is an unavailable
resolution route, not exhausted evidence authorizing an Unresolvable transfer.
Matching and opt-in current-catalog annotation removal are unchanged.

Prepared author displays also reuse the existing display harmonization routine
after credit/order preparation, including canonical dot-and-space initials.
This does not add a second typography parser or rewrite raw provider evidence.
Existing canonical names converge without author writes; a normalization collision
with another credited author withholds the field rather than silently dropping one.
Initial-spacing changes reuse compatible raw provider cache entries.

Bounded Domain pseudonym and identity-triage checks use the non-backtracking regex
engine, not short wall-clock timers. Existing field limits remain in force.
Marker boundary consumption is used only for boolean checks; bare-suffix removal
retains its original match span. Debugger/scheduling delays therefore cannot turn
these checks into regex timeout failures or missing-marker evidence. Their matching
semantics and policy/cache versions are unchanged.

WPF read-only scan/load/review commands also contain regex timeout failures at
their operation boundaries, reporting `ANALYSIS.REGEX_TIMEOUT` through existing
user-visible logging instead of forwarding a faulted async command to the
dispatcher. They do not publish successful review state or retry automatically.
The combined workflow's boundary stops applying once mutation is entered; it
cannot relabel a mutation failure as unchanged analysis. Cleanup additionally
requires the authoritative snapshot to be the one currently displayed, including
after a failed load that already changed the application-owned state.

Infrastructure persists only bounded Apply overrides in an atomic path-private file;
no proposal fields, local metadata, provider payloads, API keys, or library paths are
stored. WPF presents one recycling/virtualized subject grid with current, proposed,
and final merged details, confidence/reasons/provenance, Apply, review filters, and
cover availability/reference text.

Candidate cleanup first transfers/removes reviewed duplicate records and advances to
a post-cleanup checkpoint. A separate confirmed metadata-only worker run accepts the
current compatible checked review subjects. Infrastructure stages trusted Open Library
covers through bounded HTTPS into an execution-scoped directory outside the library.
The fixed persistent worker merges identifiers from the live Cache, verifies stored
local fields and exact read-back, and returns the live managed path and author sort for
state projection. Provider/model evidence still has no direct mutation authority.

After metadata completion, a separate final-author review screens retained metadata,
reuses fingerprint/version-bound EPUB/PDF observations, and selectively escalates to
three EPUB spine documents or five isolated-PDF pages with no retained prose. Atomic
bounded caches store only author/title candidates, roles, locations, coverage, hashes,
versions, and technical status. WPF presents a virtualized checked review bound to
current state identity. Final execution isolates author writes, then protocol 1.7
invokes public Calibre `update_path` for retained formatted records and projects only
verified actual paths/basenames plus unchanged format fingerprints.

Managed-name worker failures retain the exact phase (`before_readback`, `preflight`,
`update_path`, `clear_caches`, `after_readback`, `verification` or `rejection_readback`), a fixed error
category and bounded numeric errno/Windows error when available. Worker-owned
validation codes retain their meanings; exception messages, paths, metadata and
tracebacks are never copied into these diagnostics. The blocking UI issue identifies
the failed record ID and requires explicit Scan. Successful results inside the
failed chunk are not projected or inferred as a committed prefix.

Protocol 1.7 additionally distinguishes a verified-unchanged managed-name rejection
from a failed mutation. On Windows, a contained read-only delete-sharing access
probe checks current formats, covers, OPF and extra files without writing/deleting;
it closes every handle immediately and does not replace the supported Calibre
update. A rejected probe or `update_path` may become a skip only after cache clear
and exact complete fresh pre/post equality, including path, identity/timestamps,
ordered author sorts, registered format inventory/names/bytes/observations,
cover/local/custom values, extra files and composites. A successful update may
legitimately change Calibre's `last_modified`; rejection proof may not ignore it.

Skipped naming uses a typed `SkipManagedNameLibraryStateDelta`, preserves the
snapshot and advances only operation accounting. Counts, technical record IDs and
reasons remain separate from metadata skips and refreshed/verified naming IDs.
WPF reports these skips and continues into disposition for the other records.
Skipped naming remains pending/unverified and cannot be accepted as Qualified
without independent verified layout. Changed/unreadable states still stop;
there is no retry, skipped-count quota or automatic transfer for missing coverage.

Unresolvable presentation defaults to concrete review problems and checked moves,
with a single optional all-books view. A bounded review-only Domain policy reuses
current catalog triage and adds delimiter-separated complete author credits in
title text and bracketed numeric author annotations. Application also identifies
exact reversed title/author pairs from explicit opening-page credits. These
observations neither alter matching nor replace final qualification evidence or
set automatic transfer defaults. Apparently normal unverified books remain
internally pending and counted without being labelled damaged in the problem list.

WPF translates problems to plain-language descriptions and next steps, separates
automatic-verification gaps and optional technical evidence, and exposes one Move
checkbox rather than read-only Manual/qualification status controls. Check all shown
uses the existing explicit per-ID inclusion port for exactly visible records;
it does not call the restricted policy-proposals-only bulk helper. Hidden records
are unchanged. Empty/already-checked views disable the action. Actual moves retain
the current review identity, backup confirmation and fixed worker boundaries.

Find repairs for selected captures the current selected IDs (or the current row)
once and passes them through Application demand planning, local-credit repair and
metadata subject publication. A nonempty distinct current scope is mandatory for
identity repair; omitted/invalid scope never falls back to all suspect books.
The ordinary post-cleanup metadata preparation remains full-library. Selected
books are queryable even when their problem is a review-only signal rather than an
older triage reason. Provider variants, request budgets and cache identities remain
unchanged, so a selected book may legitimately require several HTTP requests.

Repair progress/results identify the selected record/count. Only those subjects
appear in Metadata review and all start unchecked, including on decision-store
failure. Scoped decision writes replace only the represented subjects' overrides,
preserving unrelated saved choices; workspace selection copies retain provider
results and deferred counts. Quarantine reassessment replaces selected evidence
while preserving unselected evidence, every checked/unchecked move and full
accounting. A checked policy suggestion that loses automatic support during
read-only reassessment remains checked as a reviewed choice, not as evidence of
obsolete automatic transfer authority. Metadata Apply checkbox changes and
presentation/view changes do not clear quarantine choices. Existing mutation,
new-generation and restart boundaries still invalidate/rebuild transient review;
this is not a new resumability or recovery feature.

Metadata repair application and quarantine movement use separate WPF commands,
checked counts and confirmation services. The shared operation runner receives an
explicit metadata-repair intent; the ordinary final disposition action cannot be
redirected by the mere presence of repair proposals. Quarantine completion discards
obsolete repair proposals. Worker progress names the actual metadata/author/naming
phase; a naming pass explicitly says it is not quarantine.

Author/identity review reuses intrinsic metadata from compatible versioned
assessments before requesting a dedicated metadata parser. Verified naming changes
do not invalidate content-derived author facts when format/fingerprint and all
analyzer/resource versions still match; path/observation checks remain strict at
actual file-inspection and mutation boundaries. Evidence lookup is indexed by
record or format/fingerprint, not a full collection scan per book. Per-run and
current-workspace inspection caches use complete format/fingerprint/extractor/
limit identities, store shared observations once and retain unselected facts.
Repeated stages can reuse current unavailable observations without inventing
successful evidence; an explicit selected repair may retry unavailable local facts.
Fresh-workspace cache corruption/loss remains a miss. No raw prose or global
mutable memoization is added.

Preparing quarantine rows uses current catalog state and existing observations
only; it does not call whole-library front-matter inspection or providers. Missing
certification remains explicit in optional accounting without delaying a manual
move list. Find repairs can inspect opening pages only for its validated selected
IDs, retaining unselected observations. Explicit manual movement uses the existing
quarantine executor, which validates and copies/verifies/deletes only checked
records; no metadata repair or naming operation is inserted into that action.

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

## Final retained-record qualification

Final author review is not whole-record qualification. After final naming,
Application builds one disposition row per retained record using an indexed
assessment/front-matter pass. Domain separately owns bounded field-sensitive
identity triage and deterministic qualification. Evidence explicitly distinguishes
unfinished, unavailable, deferred, conflicting and completed routes.

The existing front-matter boundary now returns reduced title/subtitle/byline pairs
with unit/line provenance, not retained prose. EPUB inspection remains within three
documents; PDF remains within five pages in the isolated worker. Extractor identity
includes the paired extraction and credit-resource policies; old author-only cache
entries cannot establish paired coverage. Unchanged completed warm observations
avoid parser startup; transient unavailable observations can be retried once per
deduplicated demand in a later run.

Targeted repair uses the existing edition-provider pipeline and review surface.
Title-only retrieval cannot establish high confidence. Final metadata repair writes
non-author fields first, then passes explicitly reviewed authors to the existing
final-author action. Naming and shared-author reconciliation precede reassessment.
No analysis path opens a mutation worker.

Review binds an immutable in-memory structural identity over current catalog values,
complete formats, observations and relevant versions. That value does not print its
bibliographic fields, is not a new persistence system, and performs no file hashing.
Existing generation/revision and worker identity checks remain mandatory.

The Unresolvable surface shows all retained records, explicit reviewer acceptance,
route coverage and frozen accounting. Only verified source removals increase the
moved count; exclusions stay pending. Cross-library mutation and destination handling
remain the existing copy-all/verify-all-before-delete boundary.

Path-changing metadata and final naming return verified registered basenames and
hash-bound current file observations through mutation protocol `1.6`. Projection
stores catalog filename stems separately from complete relative filenames and
preserves Present status only with verified observations. Relative-path comparison
normalizes native/canonical separators; identical-fingerprint assessments remain
reusable after supported naming. No second scan or filesystem mutation fallback is
used to hide an incomplete projection.

For explicitly isolated/offline runs, composition accepts
`CALIBRE_CLEANER_DATA_ROOT` and `CALIBRE_ONLINE_METADATA_ENABLED=0`. The normal data
root and online defaults are unchanged. Configured storage is physical and outside
Calibre libraries; offline edition resolution short-circuits before key access.

## Current architectural debt

- Cached SHA-256 identities have forced verification but not periodic/selective byte
  revalidation or a dedicated UI command.
- Current mutation state is more detailed than the accepted minimal target.
- Full compatibility scan code remains for compatibility although staged mode is the
  configured product workflow.
- PDF matching, additional online providers, local ML, incremental candidate recomputation, and
  calibrated evidence fusion are not implemented.
