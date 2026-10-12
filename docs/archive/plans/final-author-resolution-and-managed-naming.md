# Final author resolution and Calibre-managed naming

## Objective

Finish the existing one-time cleanup workflow with reviewed author corrections and
Calibre-managed naming. Retained books use the display-author name, a sanitized
title with the existing record ID, and Calibre-generated format filenames:

```text
Author display name/
  Book title (record ID)/
    Book title - Author display name.epub
```

Preserve reliably known authors, especially successfully applied online corrections.
Use `Unknown` only when available, adequately inspected evidence cannot establish an
author. An embedded `Unknown` never overrides a reliably known author.

Status: implementation plan only. Creating this plan does not authorize library
mutation or begin product implementation.

## Scope

- Extend the existing final author-normalization action after online metadata
  application, rather than insert mutations into Exact or Candidate analysis.
- Screen retained records cheaply for missing, placeholder, malformed, number-like,
  and title-like author values. Preserve conservative name-order normalization.
- Reuse compatible EPUB/PDF metadata observations; inspect only unresolved suspect
  records, escalating to bounded front matter when cheaper evidence is insufficient.
- Preview current/proposed authors, relevant title changes, evidence, reasons, and
  Apply/Skip decisions before mutation.
- Apply approved title/author metadata through the fixed Calibre worker, then verify
  live paths and registered format availability.
- Keep book folders exactly two levels below the library root. Keep existing book
  IDs, including under a single `Unknown` author folder.
- Report unsafe, stale, unregistered, or unsupported layout cases without guessing
  how to repair them.

## Out of scope

- Changes to duplicate matching, calibration, keeper selection, or grouping authority.
- Merging records because both have author `Unknown` or equal titles; creating
  artificial authors such as `Unknown1`; changing record IDs.
- Renaming, flattening, moving, or deleting `.calnotes`, `.caltrash`, or other
  identified Calibre-internal storage.
- Automatically deleting/reimporting uncatalogued folders or unregistered files.
- Broad title reconstruction, arbitrary personal-name reordering, library-specific
  aliases, new online providers, cloud AI, OCR, or whole-book text extraction.
- Direct SQLite writes, filesystem renaming, alternate mutation engines, temporary
  metadata tricks to force renames, or editing embedded ebook bytes as part of naming.
- General cache/state redesign, periodic maintenance, backup management, rollback,
  recovery, or resumable analysis.

## Relevant requirements

- [Repository instructions](../../../AGENTS.md): read-only analysis, worker-only
  mutation, external-backup confirmation, privacy, bounded resources, and dependency
  direction.
- [Functional requirements](../../functional-requirements.md): separate Candidate and
  metadata runs; explicit final author normalization; operation exclusion.
- [Safety assumptions](../../safety-and-rollback.md): supported path updates, verified
  metadata/local-field preservation, stop on ambiguity, and explicit Rescan.
- [Architecture](../../architecture.md), [test strategy](../../test-strategy.md), and
  [roadmap](../../roadmap.md): reuse the accepted one-time cleanup baseline; do not
  reopen matching calibration.
- Accepted ADRs [0001](../../adr/0001-read-only-calibre-access.md),
  [0002](../../adr/0002-clean-architecture.md),
  [0015](../../adr/0015-persistent-calibre-api-mutation-worker.md), and
  [0021](../../adr/0021-matching-first-performance-oriented-product.md).
- Current user decisions: allow `(ID)` suffixes; ignore internal storage; preserve
  separate records under `Unknown`; keep correct online authors; inspect books
  selectively rather than reopen the entire library.

## Existing implementation inspected

- `MainWindowViewModel` runs Candidate cleanup, prepares/applies online metadata,
  then exposes `Normalize author names` while the checkpoint phase is `Completed`.
  Command availability currently depends only on conservative normalization count.
- `ExecuteUnifiedCandidateCleanupUseCase` separates Candidate, metadata, and final
  normalization runs. `BuildAuthorNormalizationOperations` currently reads snapshot
  authors only; it does not resolve incorrect authors from book evidence.
- `PipeAuthorNameNormalizationPolicy` converts only unambiguous pipe/comma forms
  with compatible exact per-author sort values. Mixed/ambiguous books are unchanged.
- `calibre_mutation_worker.py` applies supported metadata with `do_path_update=True`,
  preserves protected local fields, and returns verified managed paths and sorts.
  Existing generation/delta/checkpoint safety remains in place.
- `EpubInspectionResult` exposes embedded title/authors. `EpubFeatureSummary`
  supports retained author observations. PDF contracts and Domain metadata summaries
  support bounded embedded author values; content-derived authors are not assumed
  to be available in existing assessment caches.
- Existing assessment caches and PDF process isolation provide reuse and safety
  boundaries. Do not bypass the isolated PDF worker for supplemental inspection.

A read-only diagnostic on the user-supplied working copy observed 17,283 records,
86 title-like author flags, 74 number-prefixed author flags, 68 pipe-containing
author records, and 65 unknown/blank-author flags. Categories overlap and are not
ground truth. All registered book folders already had IDs and two-level paths;
deeper directories belonged to internal storage. These measurements scope manual
acceptance only; they are not automated test fixtures or author aliases.

## Proposed design

### Workflow placement

```text
Exact analysis/cleanup
Candidate analysis/cleanup
Online metadata review/application
Prepare final author/naming review (read-only)
Apply checked final corrections and verify Calibre-managed names
Finished
```

Broaden the existing final action instead of adding a separate cleanup subsystem.
Preparation must work even when no pipe/comma normalization candidates exist. Review
and apply are distinct activations; preparation never starts a mutation worker.
Use current checkpoint/state mechanisms and serialized-operation coordination.
Distinguish metadata completion from final-review readiness in the UI without a
broad workflow-state redesign. Apply must never restart duplicate grouping.

### Cheap suspicion policy

Screen current retained metadata in one bounded pass, with deterministic reason
codes and no book-file opens. Signals include blanks/placeholders, parenthesized
numeric tokens such as `(1922)`, number-prefixed values, exact normalized author/title
equality, strong title-token resemblance, and malformed combined-author separators.

Use existing text-normalization helpers where suitable, but keep this policy separate
from matching. Similarity is a triage signal, not a replacement-author generator.
Do not reject every numeric token, reverse every name, or identify a real author by
looking up a title-like string alone. Biographies, mononyms, corporate authors,
initials, cultural name order, translators, and legitimate coauthors need fixtures.

Return known/usable, suspect, unresolved, or conflicting status plus reasons and
evidence coverage. A signal never by itself authorizes setting `Unknown`.

### Evidence precedence and preservation

1. Preserve current authors successfully written and verified from approved online
   metadata. Do not mistake a selected but failed/skipped provider proposal for an
   applied correction.
2. Preserve a credible current author unless stronger evidence establishes a
   correction. Compatible full-name/order evidence may support normalization, not
   an unrelated author substitution.
3. For suspect/unresolved records, consider compatible cached EPUB/PDF author
   metadata, then cheap embedded-metadata reads if needed.
4. For still-unresolved selected records, inspect bounded title/copyright/byline
   information. Keep author roles and conflicting sources visible.
5. Propose `Unknown` only when no reliable author remains and coverage is adequate.
   Unreadable, encrypted, unsupported, stale, or limit-exceeded input stays
   unresolved/unchecked; absence of accessible evidence is not proof of no author.

Embedded `Unknown` is missing evidence, not a higher-priority author. Lower-priority
disagreement with a known author cannot silently replace it. Cross-format disagreement
and ambiguous author/editor/translator roles require review; never combine every
person mentioned into the author list.

Reuse verified metadata-operation source information from the current workflow where
available. Establish how that protection survives reload before enabling writes.
If exact applied provenance cannot be reconstructed, preserve credible current
authors and flag uncertainty rather than inventing provenance. Any additional
protection marker must be minimal, identity-bound, and part of existing state, not
a new general history/provenance service.

### Selective, bounded inspection

- Prefer existing compatible feature summaries, not a second full assessment.
- On cache misses, read EPUB package metadata or bounded PDF Info/XMP metadata.
  Parse author roles; do not treat PDF Creator/Producer software as an author.
- Escalate only unresolved records to front matter. Initial ceilings: up to three
  EPUB spine documents and five early PDF pages, with at most 32,768 useful text
  characters per document/page. Existing stricter archive, XML, decompression,
  process, memory, CPU, and wall-time bounds still apply.
- Treat those ceilings as maximum coverage, not guaranteed author discovery.
  Detect explicit byline/author-role candidates conservatively; incidental name
  mentions remain ambiguous. Do not run OCR or fetch referenced resources.
- Reuse existing EPUB/PDF parsers. New orchestration/policy is C#; any PDF capability
  belongs behind the existing isolated worker. The fixed Calibre Python worker
  remains the sole mutation boundary.
- Bound concurrency and keep progress truthful: records screened, cached evidence
  reused, metadata inspected, front matter inspected, unresolved/conflicting records,
  completed units, and elapsed time. Do not add arbitrary cancellation points.
- Do not retain raw page/chapter text. Retain only bounded author candidates,
  source/role/location, coverage, and technical reason/version facts needed for review.
- Reuse existing persistence/cache abstractions. Any added observation must be keyed
  by exact file identity/fingerprint, parser/extractor/policy versions, and limits.
  Changed input invalidates observations; corrupt/lost cache is a miss. No general
  cache framework or content archive is introduced.

### Final review and execution

Reuse the existing metadata-review patterns: current/proposed values, Apply/Skip,
reason/source/coverage, and efficient multi-selection. Default-select only
unambiguous conservative corrections validated by fixtures; content-derived
corrections, `Unknown`, and conflicting/partial evidence require explicit selection.
Protect approved online author corrections regardless of stale embedded placeholders.

Bind review to record ID, current authors/title/formats, input identities, generation,
revision, and policy versions. Reject stale selections before mutation. Persist
bounded choices through existing compatible decision storage where suitable; do not
repurpose edition-provider confidence as local-author confidence.

Preserve current/approved titles unless there is a separately evidenced, approved
title correction. Delegate sanitization, transliteration, truncation, primary-author
directory selection, and filename construction to Calibre; UI path previews are
advisory until worker read-back. Keep all linked coauthors in metadata even when
Calibre names the directory after its primary author.

Use typed title/author updates through the existing worker. Preserve verified local
fields, unchanged metadata, format fingerprints, and exact author sorts where the
existing conservative policy provides them. Keep existing isolated author-write
chunks and non-author-before-author ordering. New correction plans must not use the
legacy pipe/comma precondition when it does not apply to their current author values.

For unchanged metadata but a mismatched managed name, first prove the supported
Calibre path-refresh behavior on synthetic fixtures. A no-op `set_field` is not
assumed to repair names. If a supported capability is required, add it only to the
fixed typed worker after compatibility tests; otherwise report the unsupported case.
Never temporarily falsify metadata or fall back to direct renames.

Verify actual managed path depth, expected record-ID suffix, contained physical paths,
registered format basenames/availability, and unchanged ebook bytes where relevant.
Project live paths and actual format names into existing state; do not guess them
from sanitized titles. Preserve record/format counts throughout this step.

```text
Unknown/Same title (123)/Same title - Unknown.epub
Unknown/Same title (456)/Same title - Unknown.epub
```

These remain separate records even after title sanitization/truncation. No numbered
`Unknown` authors, deduplication, or overwrite is allowed. Unexpected third-level
book directories and unregistered ebook-like files are reported for separate review,
not flattened or silently adopted by this action.

## Files expected to change

- Domain metadata policies next to `PipeAuthorNameNormalization.cs`: suspicion,
  source preservation, resolution, confidence/coverage, and immutable review facts.
- Application metadata preparation/contracts: read-only final review and bounded
  evidence demand planning; reuse assessment summaries and current review patterns.
- `ExecuteUnifiedCandidateCleanupUseCase.cs` and its contracts: approved final
  operations, stale preflight, worker read-back, and result counters.
- EPUB/PDF inspection adapters, contracts, and PdfWorker only where bounded author
  observations are missing. Extend relevant cache/state serializers and versions
  only if the new facts require persistence.
- The fixed Calibre worker and typed protocol only for necessary verified managed-name
  refresh/read-back capabilities; no new worker or write route.
- `MainWindowViewModel.cs`, existing review views/row models, and confirmation service:
  prepare/review/apply gating, broader final-action eligibility, and truthful completion.
- Neighboring Domain, Application, Infrastructure, WPF, and architecture tests.
- Current functional, safety, architecture/test, and roadmap documents at completion.
  Create an ADR only if a genuinely new integration/protocol decision requires it.

## Safety considerations

Analysis and preview are read-only. Mutation requires the application's per-run
confirmation of a complete external backup and closed competing library writers.
No additional chat backup confirmation is needed. Every operation uses the same
worker, lease, preconditions, local-field protection, and verified state publication.

Stop on failed/ambiguous mutation and require explicit Rescan. Preserve the existing
verified-unchanged rejection rule; do not retry, continue after changed/unreadable
post-state, infer a successful prefix, or switch engines. Internal storage remains
untouched. Logs contain aggregate progress/counters and opaque IDs/reason codes, not
author/title/path strings, prose, provider payloads, or credentials by default.

## Implementation steps

1. Confirm the plan before substantial implementation. Stage current relevant
   worktree changes as the requested rollback point without committing or reverting
   unrelated work. Confirm local provenance and supported path-refresh behavior with
   focused synthetic checks before expanding execution contracts.
2. Implement/test deterministic suspicion and source-preservation policies. Prepare
   a read-only final review using current metadata and compatible cached summaries.
   This slice performs no deep inspection or mutation.
3. Add cheap embedded-metadata inspection only for unresolved flagged records.
   Validate identity, parser bounds, warm reuse, and failure coverage.
4. Add bounded, selective front-matter extraction for remaining unresolved records
   behind the existing EPUB/PDF inspection boundaries. Validate roles, ambiguity,
   resource limits, and absence of retained prose.
5. Wire preview, Apply/Skip, and final-action eligibility into the existing workflow.
   Keep unresolved records unchecked, preserve known online authors, and require
   explicit selection for `Unknown`/content-derived proposals.
6. Apply reviewed corrections and necessary supported name refresh through the
   fixed worker. Verify live metadata, sorts, format names, paths, identities, counts,
   and unchanged bytes. Keep no-op/repeated preparation convergent.
7. Run focused checks after each slice, then standard verification. Measure cold/warm
   behavior on generated data and perform explicitly approved acceptance on a
   disposable library copy. Reconcile docs and archive this plan only after completion.

## Tests

- Detection: placeholders, embedded numeric/year tokens, exact/near title resemblance,
  biographies, corporate authors, initials, mononyms, multi-author/multi-separator
  strings, name particles, Unicode, and stable reason codes. Signals never authorize
  replacement by themselves; use a small labeled author-resolution fixture set,
  separate from frozen matching calibration/holdout resources.
- Precedence: verified applied online author survives embedded `Unknown`, missing
  metadata, stale caches, reload, and lower-priority conflicts. Failed/skipped
  provider writes are not represented as successful author resolution.
- Evidence: EPUB author-role metadata, PDF Info/XMP authors, Creator/Producer software,
  explicit bylines, translator/editor mentions, cross-format conflicts, and missing
  front-matter evidence. Unreadable/encrypted/limited input remains unresolved.
- Performance: clean/protected records open no book files; demand scales with flagged
  records, not all library formats; early resolution avoids deeper inspection.
  Record cold/warm duration, file opens/bytes, cache hits, worker launches, and peak
  resources; compatible warm runs reuse persisted observations.
- Parser/privacy: hostile ZIP/XML/PDF fixtures, reparse/containment failures, changed
  inputs, exact coverage/limits, worker crashes, corrupt caches, and no prose or
  bibliographic/path leakage to ordinary logs.
- Workflow: zero legacy normalization candidates still allows suspect-author review;
  preparation never mutates; operation exclusion; stale review rejection; separate
  backup confirmation; mixed Apply/Skip; truthful progress and final completion.
- Mutation: protected local metadata and ebook bytes survive; IDs/counts stay fixed;
  author sorts remain correct; worker-only calls; no retries/fallback; failure blocks
  further mutation until Rescan; repeated review schedules no duplicate writes.
- Naming: equal titles under `Unknown` retain separate `(ID)` folders; punctuation,
  case, truncation, Unicode, and primary-author cases use actual Calibre behavior.
  Test no-op metadata with stale paths, format-basename projection, unsupported
  third-level book data, extra files, and untouched internal storage.
- Regression: existing matching corpus/baselines, metadata-review convergence,
  conservative author normalization, worker safety, and dependency-direction tests.

Reuse neighboring fixtures/test classes whenever suitable. Automated tests use
synthetic/appropriately licensed books, fake providers, and TestCalibre; never a
personal library. Real Calibre supported-path acceptance is opt-in on a disposable,
externally backed-up copy.

## Verification commands

Use targeted tests first for each changed slice, then run the repository gates:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Record actual results, baseline warnings/failures, cold/warm metrics, and remaining
manual acceptance. If the running application locks output files, report the process
and wait for the user to close it; do not route builds into alternate artifact paths.

## Risks

- Title-like/number-containing author names can be legitimate; triage must not become
  automatic guessing or a matching-policy change.
- Embedded metadata and front-matter text can be stale, incomplete, or role-ambiguous;
  reliable applied metadata must not be downgraded.
- Applied online provenance may not survive current review clearing/reload in a form
  suitable for final-review protection; resolve this before executable proposals.
- Current `Completed` includes the narrower author action. Broadening eligibility
  must not silently mark unresolved work complete or loop indefinitely after Skip.
- Calibre no-op metadata writes may not refresh managed names, and title/author writes
  can change both directories and format basenames. Verify actual supported behavior.
- Deep inspection may still find no author. Scanned PDFs require manual review in
  this scope, not implicit OCR or automatic `Unknown` on parser failure.
- Independent active metadata plans may touch the same execution/review surfaces;
  coordinate compatible changes without broadening or replacing those plans.

## Resolved implementation questions

- Successfully applied author provenance uses the existing projected state: provider/
  proposal identity plus SHA-256 of exact ordered read-back authors. It survives reload,
  unrelated metadata changes, and clears on an unproven author replacement.
- Calibre 9.12 no-op `set_field` does not refresh stale names. Public `update_path`
  does, so protocol 1.3 exposes only that supported typed capability with full read-back.
- Three EPUB documents, five PDF pages, and 32,768 useful characters per unit remain
  the frozen V1 ceilings after labeled boundary fixtures and generated cold/warm demand
  measurement; no evidence justified increasing them.

No further user choice is required to begin the first read-only policy slice once
implementation is explicitly approved. Conflicts, unsupported layouts, and unresolved
authors stay visible/non-executable rather than being silently "solved."

## Progress

- [x] User decisions and existing final-normalization path inspected.
- [x] Existing metadata inspection contracts and safety/test requirements inspected.
- [x] Implementation plan created and indexed.
- [x] Implementation explicitly approved and rollback point staged where applicable.
- [x] Suspicion/source-preservation policy and cache-first review implemented.
- [x] Selective embedded/front-matter evidence implemented and measured.
- [x] Final review and worker-only naming execution implemented.
- [x] Focused/standard checks and disposable-library acceptance completed.
- [x] Current documentation reconciled and plan archived.

The first read-only slice adds deterministic suspicion reasons, conservative
source precedence, immutable generation/revision/format-bound review facts, and
strict reuse of compatible EPUB/PDF author summaries already present in the
authoritative snapshot. Clean credible records are not materialized as review rows;
unresolved/conflicting rows expose inspection demand without opening book files.
Only the existing unambiguous pipe/comma normalization defaults selected. Cached
embedded corrections remain unchecked, and embedded `Unknown` remains missing
evidence.

Successfully read-back online author writes now publish a minimal projected-state
marker containing provider/proposal identity and a SHA-256 identity of the exact
ordered verified author values. The marker survives delta-journal reload and
compaction through existing state, is preserved by unrelated metadata changes, and
is cleared by an author replacement without matching provenance. No marker is
written for local Calibre normalization. This resolves the applied-author
provenance implementation gate without adding Calibre fields or a history service.

A disposable synthetic probe against installed Calibre 9.12.0 established that a
no-op title/author `set_field(..., do_path_update=True)` reports no change and leaves
stale directories and format basenames unchanged. Public `update_path({book_id})`
then changed `Initial Author/Initial Title (1)` and its TXT basename to the current
`Final Author/Final Title (1)` and `Final Title - Final Author.txt`, preserving one
record and one format. The implemented typed worker refresh therefore uses and
verifies `update_path`; it does not use temporary metadata or assume no-op writes
refresh names. The disposable probe is retained under `scripts/` and removes its
temporary library in `finally`.

Focused results: 25 author-policy tests, 7 final-review tests, 3 projected-state
provenance tests, and the persisted provenance round-trip pass. Complete affected
project runs passed Domain 277/277 and Application 239/239. Infrastructure passed
276/277 initially; one unrelated viewer-launch test could not delete a temporary
TestCalibre DLL while its test-created process was exiting. The failed test and
provenance round-trip both passed on isolated rerun (2/2). The solution build passed
all projects. Standard whole-solution verification remains for the completed plan.

Selective embedded-metadata inspection is now implemented for unresolved demanded
EPUB/PDF formats. EPUB inspection performs existing physical identity and archive
preflight, then reads only `container.xml` and the OPF metadata before stopping; a
missing spine document fixture confirms it does not traverse content. PDF inspection
runs only in the isolated PdfWorker with the existing cleared environment, Job Object,
heap/CPU/working-set/wall-time limits, strict file/hash checks, and bounded protocol.
Its metadata request reads Info and bounded XMP `dc:creator` values without the
DocumentOpened/PageSelection exchange or page parsing. PDF Creator/Producer software
is not treated as author evidence.

Observations contain only bounded title/author facts, status/problem code, exact file
fingerprint, extractor version, and limits version. A separate 256 MiB/100,000-entry
atomic cache uses opaque SHA-256 filenames, exact identity validation, reparse checks,
and oldest-first pruning; corruption, version changes, and cache I/O failures become
misses. Final preparation validates the library once, touches only `NeedsInspection`
EPUB/PDF formats, uses cache hits before path resolution, and reports cache-hit and
inspection counters. Clean records produce no cache, path-resolution, or inspector
calls. Front-matter escalation remains the next unimplemented evidence stage.

Embedded-metadata slice verification passed the complete Application suite (243/243),
complete Infrastructure suite (283/283), focused missing-spine/cache/XMP/isolated-
worker tests, and a full solution build. Formatting required only scoped CRLF final-
newline correction on new files; standard completed-plan gates remain pending.

Front-matter work has begun with a deterministic Domain policy that accepts only
explicit line-start `By`, `Written by`, or `Author:` credits as author candidates and
retains `Editor`/`Translated by` roles separately. Conservative name-shape checks
cover initials, particles, mononyms, corporate and non-Latin names while rejecting
incidental prose, digits, mixed roles, and ambiguous separators. Provider-neutral
observations freeze the proposed ceilings at three EPUB documents, five PDF pages,
and 32,768 useful characters per unit, retaining only candidates, role, unit/line
location, coverage, truncation, fingerprint, versions, and technical problem code.

The EPUB front-matter adapter is implemented behind the existing guarded inspector.
After archive/XML preflight it reads at most the first three HTML spine documents,
extracts bounded block-level lines, immediately reduces them through the byline
policy, and returns no prose. Focused fixtures prove an explicit byline is retained,
a prose sentinel is absent from serialized output, and a byline in spine document
four is not read. PDF front matter, front-matter cache/orchestration, and final review
fusion remain pending, so the combined selective embedded/front-matter progress item
is intentionally still unchecked.

PDF front-matter extraction is now implemented only inside the isolated PdfWorker.
The strict parser reconstructs at most 200 bounded lines per page from stable text
baselines, examines no more than the first five pages and 32,768 useful characters
per page, and immediately reduces lines through the shared byline policy. The worker
protocol carries only primitive candidate/role/location/coverage facts; the parent
rehydrates and validates the immutable observation after resource monitoring and a
final physical file-identity check. Direct and isolated fixtures prove explicit
bylines, no serialized prose sentinel, and that page six is not read. Front-matter
cache/orchestration and final review fusion remain pending before this evidence stage
is complete.

Front-matter observations now use a separate typed schema in the shared bounded
author-observation cache. Exact format/fingerprint/extractor/limits identity is
required; atomic writes, reparse checks, oldest-first shared capacity pruning,
corruption-as-miss, and path/prose exclusion match embedded metadata behavior. A
separate escalation use case touches only review subjects still needing inspection,
reuses cache hits before path resolution, invokes the bounded EPUB or isolated PDF
port on misses, caches missing/unavailable outcomes as completed coverage, and
reports truthful hit/inspection counters.

Final review retains author/editor/translator candidates with unit/line locations.
Only explicit author-role facts participate in resolution; they produce unchecked
`ContentDerivedFrontMatter` proposals, conflicting author facts remain conflicting,
and editor/translator facts never become authors. An observed missing/unavailable
present format is exhausted and does not loop, while a suspect record with no
inspectable format stays visibly unresolved. Focused cache-hit tests prove warm runs
do not resolve paths or launch parsers. Implementation is complete for this evidence
stage, but the combined progress checkbox remains open until cold/warm generated-data
measurements validate the proposed ceilings and demand scaling.

Complete affected-suite verification after front-matter integration passed Domain
297/297 and Application 248/248. Infrastructure completed 289 tests before the known
viewer-launch fixture intermittently retained its copied TestCalibre DLL during
temporary-directory teardown; the failed viewer test plus all five shared author-
observation cache tests passed on an isolated no-build rerun (6/6). Formatting and
diff checks passed after scoped CRLF final-newline correction. No production library
was opened or mutated by these tests.

Final-author review subjects now carry immutable Apply choices: conservative
normalization follows its existing checked default, while embedded/content-derived
proposals remain unchecked. Bulk/selected choice helpers preserve the complete
generation/revision/evidence workspace. The Completed-phase executor accepts an
optional final-review workspace, validates library root, generation, revision,
current title, ordered author names/sorts, format paths/status/fingerprints, proposal
presence, and unique targets, then schedules only checked author updates. Content-
derived corrections intentionally omit the legacy comma/pipe author-sort precondition.
Focused tests prove one checked correction creates one author operation, unchecked
review starts no worker, and stale title identity fails preflight before worker
startup. The legacy direct normalization request remains temporarily compatible until
the WPF command is switched to mandatory preparation/review.

A dedicated WPF row model now presents record ID, current/proposed author displays
and sorts, resolution status/source/protection, stable reasons, evidence coverage,
front-matter author/editor/translator roles with format/unit/line locations, Apply
eligibility/default, and All/Checked/Needs review/Unresolved/Conflicting filters.
Apply persistence is delegated to the workspace owner, matching the existing metadata
review pattern. Focused tests cover evidence display, content-derived review defaults,
filtering, and non-actionable unresolved rows. MainWindow collection/command/XAML
ownership remains the next UI slice.

MainWindow now owns the final-author workspace and a dedicated virtualized review tab.
The first Completed-phase activation runs snapshot screening, cache-first embedded
metadata, then bounded front matter with truthful progress and no confirmation or
mutation worker. Rows expose Apply, status/source, current/proposed display names and
sorts, coverage, reasons, and role/location detail with All/Checked/Needs review/
Unresolved/Conflicting filters plus row, selected-row, Check all, and Clear all choices. The second
activation is enabled for checked corrections or retained managed naming, requires the
existing external-backup confirmation, and submits the exact reviewed workspace to
the stale-validating executor. Successful persisted loads/scans clear session-bound
review state. Focused tests prove preparation does not confirm or execute and the
second activation carries the final-review payload; complete WPF tests passed 53/53
and architecture tests passed 33/33.

The final-author grid now hands extended selected rows to the view model and exposes
Check selected/Uncheck selected alongside Check all/Clear all. Selection is validated
against the current unfiltered workspace, cleared when filtering or workspace
replacement changes row identity, and applied in one immutable workspace update.
Focused workflow and architecture tests cover uncheck/recheck before reviewed
execution and all bindings. The previously noted selected-row bulk-choice gap is
closed; remaining UI work is limited to acceptance/polish discovered during manual
use.

Managed naming now uses a distinct typed `RefreshManagedName` operation in fixed
worker protocol 1.3; it does not rely on no-op `set_field` behavior. Final execution
isolates checked author writes first, then refreshes every retained record with at
least one registered format in bounded chunks, including naming-only runs with zero
checked author corrections. The worker calls public `cache.update_path({book_id})`
and verifies supported metadata, local fields, cover bytes, format set/count, streamed
format size/SHA-256, a contained physical directory, exactly two managed path levels,
the current `(record ID)` suffix, registered physical format availability, and actual
format basenames. Any mismatch fails the chunk under existing stop-and-Rescan rules.

Protocol responses use primitive bounded format facts and are validated before Domain
rehydration. A typed state delta refuses wrong depth/ID suffix, changed format sets,
or changed fingerprints, projects only worker-returned paths/basenames, and survives
delta-journal reload. Reviewed author operations run before refresh; unchecked review
still permits explicit managed naming, and progress/results count successful refreshes.
Focused verification passed five Domain projection tests, three Application ordering/
naming/staleness tests, seven worker-boundary tests, state-store round-trip, Python
syntax compilation, and the two-stage WPF workflow test.

Disposable fixed-worker acceptance subsequently passed against installed Calibre
9.12.0 using synthetic metadata and TXT bytes. The protocol 1.3 worker changed
`Initial Author/Initial Title (1)` and `Initial Title - Initial Author.txt` to
`Final Author/Final Title (1)` and `Final Title - Final Author.txt`; record count and
format count remained one, tags/comments survived, streamed bytes and SHA-256 were
unchanged, the typed stopped handshake completed, and the temporary library/config
were deleted. The reusable acceptance harness lives under `scripts/` and requires the
explicit disposable marker.

The full solution build passed. The final full test run completed 931 of 932 tests before
the known viewer-launch fixture intermittently retained its copied TestCalibre DLL
during teardown; all managed-name tests passed, and the viewer test passed alone after
its test child exited. Formatting and diff checks passed after scoped new-file newline
correction.

The package vulnerability audit completed against NuGet.org with no vulnerable
direct or transitive packages reported for any production or test project.

A generated 5,000-record orchestration diagnostic with 200 suspect records split
evenly across EPUB and PDF measured 557 ms cold and 385 ms warm on this development
machine. Cold demand performed exactly 200 inspections, including 100 isolated-PDF
worker demands; the warm run reused exactly 200 cache entries and launched zero PDF
workers. The other 4,800 credible records caused no format demand. This fixture
measures screening/demand/cache orchestration rather than physical parser bytes or
peak process memory; parser byte/resource ceilings and worker watchdog behavior remain
covered by the existing hostile-input/resource tests.

## Final outcome

Implementation and documentation are complete. The final workflow preserves verified
authors, screens and selectively inspects suspect records, presents checked review,
applies only stale-validated corrections, and refreshes retained Calibre-managed names
through verified worker-only `update_path`. Cold/warm generated demand measurement,
complete repository gates, vulnerability audit, and disposable Calibre 9.12 fixed-
worker acceptance are recorded above. No personal library was used; release use still
requires the application's explicit external-backup confirmation.