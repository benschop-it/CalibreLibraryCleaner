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
- A managed-name rejection follows the same proof-based continuation rule, with
  complete record/file evidence rather than only the target metadata field.
  Skipped books are reported separately and do not become verified naming evidence.

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
- publishes an authoritative `ExactReady` generation when groups require review, or
  automatically advances the authoritative generation to `CandidatePreparationReady`
  when no Exact groups exist.

Exact analysis does not run EPUB/PDF assessment or Candidate matching.
The Expanded evidence view must describe that expected absence and direct the user
through Exact cleanup and Prepare Candidate review, not ask for another Exact scan.

### 2. Exact cleanup

When groups exist, the user reviews the generated keeper for each byte-identical
same-format group and may override it or Skip the group. Exact cleanup:

- is skipped automatically, without backup confirmation or worker startup, when the
  authoritative Exact scan contains no groups;
- exposes one cleanup action in the top workflow bar when groups exist;
- transfers unambiguous complementary formats to one keeper;
- removes duplicate/transferred source formats;
- removes records only when they become empty;
- leaves conflicting or ambiguous records unchanged; and
- advances to `CandidatePreparationReady` after completed cleanup.

### 3. Candidate preparation

The first Candidate action is read-only. It:

- rereads and reconciles the residual catalog against the completed Exact outcome;
- reuses explained fingerprints and target-hashes transfer destinations when needed;
- preserves unchanged invalid paths as non-executable findings;
- reuses compatible EPUB/PDF assessments and cached EPUB signatures;
- detects exact normalized metadata candidates;
- ignores explicit pseudonym annotations in matching author identities while
  preserving the published pen name and original catalog metadata;
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
present EPUB/PDF formats, but they do not own keeper/Skip choices or a mutation
command.

Double-clicking a book row opens its present EPUB, or its present PDF if no EPUB
is available. This applies to Library, Exact/Metadata/Expanded/Unified member
grids, Metadata review, and Final authors. Library format rows and EPUB/PDF
assessment rows open their specific file. EPUB opens in Calibre's ebook viewer;
PDF opens in the Windows default PDF application without requiring Calibre's
viewer. Opening validates library containment and regular-file safety first.
EPUB viewing uses an opaque bounded temporary copy outside the library and a
dedicated viewer instance, so bookmarks/annotations cannot alter scanned managed
bytes; the copy is deleted on viewer exit with bounded stale cleanup after abnormal
termination.
Group summaries and assessment findings are not book rows. Headers, empty grid
space, and embedded controls do not launch files. Opening does not change review
decisions beyond the existing single-click selection behavior.

All published groups start included. `Cleanup eligible` and `To be reviewed` are
advisory; a group is processed unless the user selects Skip. Candidate cleanup:

- requires a second external-backup confirmation;
- transfers quality-ranked complementary formats absent from the keeper;
- keeps the keeper's existing same-format file;
- removes all formats from non-keepers; and
- removes non-keeper records proven empty.

After completed Candidate cleanup, projected pre-cleanup Expanded evidence may be
absent or stale by design. The evidence-view summary must direct the user to
retained-record metadata review, not a redundant Rescan. Genuine missing evidence
in an expected Candidate review, uncertain state and incompatible checkpoints
retain their existing recovery guidance.

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

Repeated author/identity stages must reuse compatible assessment and current
workspace facts before opening formats again. Bind reuse to format, full content
fingerprint and extractor/analyzer/resource versions, not an obsolete filename.
Use indexed record/file evidence lookup and one shared observation per complete
key, including per-run reuse when persistent cache writes fail. A warm workspace
should start zero parsers and need no further metadata-cache reads for unchanged
covered facts. Do not equate this with skipping necessary copy/mutation read-back.

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
provider independently. The Open Library adapter inspects up to five works and five
nested editions per work, ranks field-rich compatible editions, and returns at most
five candidates with bounded title, authors, validated ISBNs, publisher, publication
date, language, series, and cover reference. After a strict miss, Application may try
bounded variants in order: no language, primary author, and conservative core title.
If search returns a title/author-compatible work without a usable nested edition,
Open Library may spend one additional request to inspect at most five records from
that work's supported editions endpoint. Every actual request, including this
fallback, counts against the provider limit; broader retrieval is still validated
against the original book metadata. Google Books uses a bounded
partial-response volumes query and extracts title/subtitle, authors, validated ISBNs,
publisher, publication date, and language; cover fields are not requested in this
step. One provider failure does not block the other. A separate opaque-key cache
stores only normalized proposal fields and provenance, never raw responses, local
queries, or the Google key.

Edition proposal cache reads probe recognized previous query-policy identities for
the same complete query/provider inputs. Saved positive candidates are revalidated
under current provider-edition rules without renewing their retrieval time. Provider
edition author comparison is independent of local pseudonym-aware matching. Recognized
annotations are removed from actual outgoing author query values, giving affected
queries different complete input identities without mutating catalog metadata.
Policy-1.4 and policy-1.6 misses from the unchanged provider version remain reusable
because their edition-comparison/extraction behavior is preserved. Uncertain policy-1.5 text misses
and older-provider extraction failures refresh. Compatible previous-query candidates can seed current ISBN aliases after
metadata changes; this bounded local backfill preserves newer cached evidence and
uses a versioned completion marker. Cache compatibility never disables policy,
expiry, corruption, or physical-path checks.

Known proposal-1.0 cache identities also remain reusable after author display fixes;
they do not require a new online retrieval merely because current proposal
interpretation is 1.1. Compatible promotion preserves original timestamps, failure
status/codes and source provider/query/proposal versions. Expired unavailable
outcomes still retry under the existing six-hour window.
Legacy sorted multi-author evidence cannot invent original credit order. Authors
may follow the complete independently matching current credit order; otherwise they
are withheld with an explanation, without discarding compatible non-author facts or
treating that missing coverage as exhausted resolution.

Each provider reads every query group's strict-to-broader cached prefix before any
network request. Fully cached groups publish first. Groups whose first missing
variant still requires online work remain in deterministic order for the online
pass, preserving fallback semantics and hard request accounting. Cache-first
reordering does not suppress stronger ISBN lookups or claim the remaining uncached
groups are fast.

The WPF Online metadata settings dialog discloses that providers receive ISBN only,
or title, author names, and optional language. It accepts masked Save/Replace/Clear
input and stores only DPAPI ciphertext for the current Windows user outside the
repository and library. Plaintext is never reloaded for display.

Deterministic provider fusion produces High confidence only for cross-provider shared
ISBN agreement without conflicts; Medium for one exact ISBN or exact title+author
without conflicts; Low for weaker or conflicting evidence; and Unavailable when no
provider proposes an edition. High/Medium default selected only when the concrete
merge is additive or a bounded all-caps-to-mixed-case improvement. Proposals that
drop current authors, replace a same-type identifier, or replace populated title,
publisher, date, language, or series facts default unchecked under Needs review.
Low/Unavailable remain unchecked. Explicit Apply overrides this conservative default.
Cross-provider missing fields require a shared validated ISBN; identical
same-provider edition IDs may also complete a missing field. Provenance is retained
per provider and per resulting field.

After Candidate cleanup, subject construction creates one proposal target for every
retained record. Candidate preparation, Unified review, persisted load, and projected
state updates do not invoke edition providers. An explicit post-cleanup action runs
provider resolution/fusion and prepares review subjects. The WPF review
surface shows compact current metadata, proposed metadata, and Final merged metadata
with Apply, confidence, reasons, provenance, all supported fields, and cover availability.
The final column follows Apply and the exact execution field policy: identifiers merge
by type, unmentioned identifiers remain, an existing cover wins, and unavailable
optional proposal fields do not clear populated current values.
Filters are All, Applied automatically, Needs review, and Unavailable.
The review grid supports extended full-row selection for contiguous Shift ranges and
disjoint Ctrl selections. Check selected, Uncheck selected, Check all, and Clear all
change applicable proposals in one bounded persisted decision write; all-subject
actions are independent of the active filter.

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
- Show each provider's concrete outcome and problem code on metadata review rows,
  especially distinguishing no validated match, ambiguity, rate limiting, and
  transient provider backend failure.
- Pace Google Books requests at no more than one start every three seconds by default;
  cached HTTP 429 outcomes may be selectively removed for an explicit retry.
- Accept a different edition of the same work when exact-edition metadata is
  unavailable; retrieval variants do not by themselves raise confidence.
- For an Open Library search result with no usable nested edition, permit one
  additional counted request for at most five editions of a compatible work.
- Persist each successful, NotFound, or transiently unavailable provider result as it
  completes. Reuse normal results for the configured cache lifetime and unavailable
  results for a short cooldown so interrupted review resumes without repeating failed
  requests immediately.
- When a provider request limit is reached, stop at the actual processed query count,
  report the uncached deferred count, and never count unrequested queries as completed.
  The default remains 10,000 requests for Open Library so high-coverage one-time
  enrichment can continue, while paced Google Books is capped at 200 requests per
  explicit preparation (roughly ten minutes); reruns continue from immediately
  persisted cache entries.
- Execute every non-author mutation before author metadata writes, checkpoint those
  completed operations, and isolate each author write in a one-operation worker chunk.
- Choose one coherent best edition using validated ISBN, title/author/language,
  edition compatibility, provider agreement, completeness, cover availability, and
  a bounded preference for mixed-case over all-caps title/author display text.
- Fill a missing field from another provider only when edition identity is proven.
- Propose supported title, authors, ISBN/identifiers, publisher, publication date,
  language, series/index, and cover without replacing local-only fields.
- Default High/Medium proposals checked only for additive/casing-improvement merges;
  require review for replacements of populated edition facts. Keep Low unchecked.
- Compare proposals with current retained metadata before review and execution. Mark
  fully equal proposals Already applied, keep them unchecked/non-actionable, and
  schedule only fields whose resulting supported value differs.

### Review and coordination

- Show current, proposed, and final merged metadata with Apply, confidence/reasons,
  provenance, supported fields, and cover indication/preview.
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
- After metadata completion, prepare a separate read-only final author review for all
  retained records. Preserve verified online authors and credible current metadata;
  screen deterministic placeholder/number/title/separator signals; reuse exact-key
  EPUB/PDF metadata; and inspect bounded front matter only for unresolved demand.
  Keep editor/translator roles visible without promoting them to authors. Only
  conservative pipe/comma normalization and deterministic display typography default
  checked. Pipe normalization may discard only an empty or strict `YYYY-YYYY`/`YYYY-`
  life-date suffix when the independent sort value agrees. Typography normalization
  uses NFC, straight apostrophes, canonical leading initial periods/spaces, and
  repeated-period repair while excluding title-like, qualified/parenthetical,
  ambiguous, reordered, expanded, or alias-based names.
  Embedded/content-derived corrections require explicit Apply and unresolved/conflicting
  rows remain unchecked.
  Recognized pseudonym annotations also produce unchecked removal proposals using
  full catalog names; unrelated qualifiers and malformed annotations are not
  guessed away. These proposals preserve pen names, never substitute real names,
  and do not trigger an early metadata mutation or a new workflow stage.
- Metadata review must not reintroduce provider pseudonym annotations into cleaned
  authors. Recognized `pseud.`/pseudonym markers and the explicit `psued.` misspelling
  withhold the complete provider author field, even for checked metadata. Retain the
  current complete author list, allow independently applicable non-author fields,
  and show the original provider credits only as evidence with an explanation.
  Do not infer a real author or silently clean/partially apply provider coauthors.
- Prepare individual provider `Family, Given` names and alternating
  `Family| Given| Family| Given` pairs as given-first display names, keeping separate
  ordered author values. Reuse the same prepared values in Proposed authors,
  final preview, checked mutation and final-author handoff. Author-sort values remain
  a distinct family-first concern. Extra/ambiguous components and qualifiers are
  withheld rather than guessed away. Reordering/splitting credits is not merely
  a casing improvement and does not become automatically checked on that basis.
- Use the same deterministic typography policy for prepared provider authors and
  Final authors: initial blocks retain a dot and space between initials
  (`A.C. Baantjer` prepares as `A. C. Baantjer`). Canonical current spacing must not
  be compacted by a checked metadata row or treated as unapplied work merely because
  the provider uses compact initials. Preserve raw provider typography as evidence.
- A separately confirmed final mutation applies only checked stale-validated author
  corrections, then refreshes every retained formatted record through the fixed
  worker's typed Calibre `update_path` operation. Verify and project actual two-level
  `(record ID)` paths, registered basenames, format availability/count, unchanged
  fingerprints/bytes, and preserved metadata/local fields. No-op metadata tricks,
  direct renames, retries, or fallback engines are permitted.
- Do not stop useful managed-name work merely because one book's update is rejected
  without changing it. Report that book as skipped and continue other records only
  after complete fresh pre/post read-back proves exact unchanged state. Check
  Windows sharing locks read-only before calling Calibre; this avoids attribute
  side effects of a rename that Calibre cannot perform. Do not retry the update.
  Changed or unverifiable outcomes still stop and require explicit Scan. Keep
  skipped naming counts/IDs/reasons distinct from metadata skips and actual naming
  successes. Skipped naming remains pending/unverified during disposition, without
  preventing assessment of other retained records.
- After final author and managed-name cleanup succeeds, prepare a final reviewed
  disposition for every retained record, independently assessing title and author
  identity, usable content, and managed layout. Outcomes are Qualified, Repairable,
  Needs review, or Proposed Unresolvable. Catalog plausibility, absent final-author
  subjects, and exact placeholder labels alone do not establish qualification or
  authorize a default transfer.
- Showing the quarantine selection list must not start another library-wide
  identity/document inspection or online repair search. Build it from current
  catalog values and already available evidence. An explicit checked quarantine
  move does not require opening-page identity certification of every retained
  book; it requires fresh selected-record identity/format preconditions, backup
  confirmation and the fixed worker's copy/read-back/delete verification.
  Leave unverified other records pending in optional accounting, not blocked behind
  mandatory whole-library inspection. Optional Find repairs inspects only its
  selected books and preserves observations/choices for the others.
- Reuse bounded opening-page EPUB/PDF inspection for paired title/subtitle and
  ordered author candidates with unit/line locations. Unlabelled pairs remain
  uncertain. Editor, translator, illustrator, foreword, publisher and dedication
  credits do not become author credits. Unsupported, image-only, limited and
  unavailable inspection remains incomplete, not proof of corruption.
- Duplicate EPUB package manifest IDs must produce a per-book unavailable finding,
  not abort final review or silently choose one declaration. Preserve complete
  retained-record accounting and expose the technical problem code in that book's
  reasons. This remains incomplete evidence, never an automatic transfer decision.
- Track applicable local/provider routes as not needed, not attempted, completed
  with/without evidence, conflicting, unavailable, or deferred. A material identity
  problem may default to transfer only after applicable routes complete; confirmed
  unusable content without a usable companion may also be proposed. Unavailable
  providers, missing keys, caps and timeouts are not completed negative findings.
  Conflicts and unresolved companion defects require review.
- Offer targeted read-only identity repair from the existing Unresolvable review.
  The Find repairs for selected action must query and publish results only for the
  selected rows, or the current row when there is no multi-selection. Disable it
  without a current selection. Require explicit validated scope in Application;
  never widen a missing/invalid selection to other suspect library records.
  Progress identifies the record ID or selected-record count, and reports actual
  cache hits/HTTP requests. One book can require multiple bounded provider queries.
  Use a unique explicit local title/byline pair when available; suspect authors may
  use bounded title-only retrieval. Title-only alternatives remain ambiguous and
  title-only provider agreement stays Low/unchecked. Existing bibliographic budgets,
  field disclosure, caches, provenance and applicability policies remain in force.
  Local identity repair searches use text validation rather than blindly trusting
  a catalog ISBN that may identify a different publication.
- Final repair review starts unchecked.
  Metadata repairs and quarantine moves have separate explicit actions and
  independent checked counts/confirmations. Preparing or checking repairs must
  not replace a quarantine move button with a metadata-update action.
  Only selected records appear in the repair workspace; preserve unrelated saved
  Apply decisions, unselected quarantine choices/evidence and complete accounting.
  A read-only repair search must retain every checked and explicitly unchecked
  quarantine move, including the selected repaired record. Checking a Metadata
  proposal, switching tabs or changing the problem view is not a request to clear
  those moves. Reassessment may replace evidence but not the user's move choices.
  Preserve provider provenance through Apply changes. Apply reviewed non-author fields through
  the metadata-only worker action; then carry reviewed author proposals into Final
  authors for a separately confirmed author/managed-name action and shared-author
  reconciliation. Reassess actual current values afterward; never rewrite already
  applied fields merely to advance review.
- Permit explicit reviewer acceptance of title, complete credits, retained content
  and disclosed format defects after inspection, and explicit manual transfer by
  record ID or individual selection. In Unresolvable review, **Check all shown**
  selects exactly the visible books as an explicit quarantine choice, including
  previously unchecked problem rows; hidden books are untouched. Disable it when
  no visible unchecked books exist. Uncheck all clears every move choice.
  Exclusion/Skip is not acceptance.
- Mutation progress must name the actual current operation: checked metadata,
  final-author corrections or full managed-name/byte verification. Do not label
  every phase unified Candidate cleanup or imply that a naming pass moves
  quarantine records. Already-canonical names can leave no visible path change
  while the required hash/read-back work still progresses.
- Present Unresolvable as a problem-focused list, not seven overlapping qualification
  filters. Default to detected catalog problems, confirmed unusable content and
  checked moves; offer one optional Show all books checkbox. Proper-looking books
  lacking opening-page identity/readability/naming proof must not be presented as
  diagnosed bad metadata. Keep internal qualification and complete accounting intact.
- Describe the suspected problem and inspection action in plain language. Review-only
  signals reuse bounded current title/author triage and flag complete multi-token
  author credits attached to title text by explicit separators, numeric bracketed
  author annotations, and explicit opening-page evidence of swapped title/author
  fields. These warnings do not change matching or automatically authorize moves.
  Legitimate author mentions, single-word credits and life-date ranges are not
  newly classified as corrupt metadata by those extra checks.
- Use a single editable Move checkbox per book. Manual assignment and internal
  qualification are not checkbox columns. Put technical codes, evidence coverage,
  optional reviewer certification and ID-based assignment in clearly labelled
  optional details/tools. Explain missing verification separately from defects.
- Freeze retained-input accounting for the final pass. A finished high-quality
  source requires `retained input = qualified remaining + verified moved`, with
  zero pending records. Existing destination records are not new moves. A completed
  transfer with pending source records is not a completed high-quality library.
  Decisions bind current metadata, complete format identity, policies, generation
  and revision; changed evidence requires rebuilt review.
- Derive the destination as the fixed physical sibling
  `<source name> - Unresolvable`. Create it through the supported Calibre API when
  absent or bind/reuse its exact UUID when present. Never reinitialize, merge, or
  overwrite existing records. Missing or incompatible custom-column schemas in an
  existing destination are blocking.
- The fixed worker copies all checked records with duplicate creation enabled,
  verifies ordinary/local/custom metadata, derived composites, per-author sorts,
  covers, extra files, formats, sizes, and SHA-256 values, and only then deletes
  verified source records. Any failed or ambiguous copy/delete stops and requires
  Rescan; no fallback, retry, rollback, or inferred successful prefix is allowed.
- Reconcile Calibre's shared-author side effects read-only after final author mutation
  and before quarantine review. Project exact live ordered author IDs/names/sorts and
  book author sorts for every affected record, but reject any simultaneous non-author
  catalog drift and require Rescan.

## Explicit non-goals

- Preserving every edition as a separate executable group.
- Application-managed backup, undo, rollback, or recovery.
- Guaranteed arbitrary cancellation or restart of partial analysis.
- Direct SQLite writes or direct Calibre-managed filesystem mutation.
- Cloud AI as matching or mutation authority.
- Further duplicate-matching calibration, new matching providers, periodic
  maintenance, generalized cache work, synchronization, plugins, or multi-library
  comparison.
