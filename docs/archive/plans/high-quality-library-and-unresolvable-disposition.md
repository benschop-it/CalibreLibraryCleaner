# High-quality library and Unresolvable disposition

## Objective

Finish one messy-library cleanup with two usable Calibre libraries:

1. The selected source contains books with trustworthy title and author identity,
   usable content, and verified Calibre-managed `Author -> Book title -> Book`
   organization.
2. The existing same-parent `<source name> - Unresolvable` library receives reviewed
   records that cannot meet that bar after bounded resolution attempts, or that the
   user explicitly assigns there.

Unresolvable describes the outcome of this cleanup, not proof that a book can never
be identified. Do not discard difficult books or build a third permanent repository.
Calibre's supported naming includes record-ID suffixes, filename sanitization, and
path-length truncation. Preserve these conventions rather than promising literal,
suffix-free paths or interpreting truncated directory names as original metadata.

## Scope

- Assess every retained record after duplicate cleanup, not just suspicious authors.
- Detect general author/title identity problems from catalog fields, managed labels,
  embedded metadata, bounded EPUB/PDF opening pages, and bibliographic providers.
- Attempt supported metadata repair before recommending Unresolvable.
- Extend the existing Final authors, metadata review, and quarantine workflow; keep
  analysis, reviewed corrections, managed naming, and cross-library mutation separate.
- Track evidence coverage and distinguish confirmed issues, exhausted resolution,
  contradictory evidence, and unfinished/unavailable inspection.
- Measure candidate recall, incorrect corrections/dispositions, and cold/warm cost on
  independent labeled fixtures before accepting a personal-library sample.

## Out of scope

- Library-specific name lists, author aliases, title lists, directory exceptions,
  or rules tied to a particular novelist, series, or library path.
- Changes to frozen duplicate matching, grouping, keeper scoring, or thresholds.
- Direct filesystem folder moves, direct SQLite writes, a new mutation engine, or
  a second application-managed destination state/synchronization system.
- OCR, rendering, cloud AI, book uploads, unrestricted web scraping, or new external
  dependencies. Scanned PDFs can be confirmed manually; OCR needs separate approval.
- Literary/content merit scoring, mandatory cover/ISBN completeness, generalized
  maintenance, cancellation/resume features, backup management, undo, or recovery.
- Automatic deletion of unidentified records or silent removal of troublesome formats.

## Relevant requirements

- User decisions, 2026-10-23: two quality-separated repositories; general patterns
  rather than sample-specific rules; use first-page evidence and book-related APIs;
  detailed diagnostic logging is acceptable for this investigation.
- [Product vision](../../product-vision.md), [functional requirements](../../functional-requirements.md),
  [architecture](../../architecture.md), [safety](../../safety-and-rollback.md),
  [test strategy](../../test-strategy.md), and [roadmap](../../roadmap.md).
- Accepted ADRs [0004](../../adr/0004-select-versone-epub-inspection-stack.md),
  [0009](../../adr/0009-select-isolated-pdf-inspection-stack.md),
  [0015](../../adr/0015-persistent-calibre-api-mutation-worker.md), and
  [0021](../../adr/0021-matching-first-performance-oriented-product.md).
- This requested disposition slice extends the current one-time cleanup workflow;
  it does not reopen the roadmap's frozen matching work or authorize OCR/cloud AI.
- [Metadata review convergence](../../plans/metadata-review-convergence.md) remains independent:
  reuse its field applicability policy rather than reintroducing repeated writes.

## Existing implementation inspected

- [AuthorSuspicionPolicy](../../../src/CalibreLibraryCleaner.Domain/Metadata/AuthorSuspicionPolicy.cs)
  already detects missing/placeholders, numeric prefixes, parenthesized numbers,
  title overlap, malformed separators, and explicit pseudonym annotations. `Known`
  means no recognized suspicion, not independently established authorship. An
  unmatched opening parenthesis or a catalog label can escape these checks.
- [FinalRecordDispositionPolicy](../../../src/CalibreLibraryCleaner.Domain/Metadata/FinalRecordDispositionPolicy.cs)
  proposes unresolved/conflicting authors, selected exhausted author suspicions, and
  exact placeholder labels. It has no complete title/content-quality qualification.
- [PrepareQuarantineReviewUseCase](../../../src/CalibreLibraryCleaner.Application/Metadata/PrepareQuarantineReviewUseCase.cs)
  visits all books but treats a missing final-author subject as `Known`. Its
  `inspectionExhausted` derives from author-review state rather than explicit
  per-route coverage. It creates included-by-default proposals and supports manual
  inclusion/exclusion, revision binding, and the fixed sibling destination.
- [FrontMatterAuthorInspection](../../../src/CalibreLibraryCleaner.Application/Metadata/FrontMatterAuthorInspection.cs)
  and [inspector ports](../../../src/CalibreLibraryCleaner.Application/Abstractions/IFrontMatterAuthorInspectors.cs)
  carry bounded, role-aware author candidates and provenance. Current limits are
  three EPUB documents, five PDF pages, and 32,768 useful characters per unit.
- [InspectFrontMatterAuthorsUseCase](../../../src/CalibreLibraryCleaner.Application/Metadata/InspectFrontMatterAuthorsUseCase.cs)
  inspects only `NeedsInspection` EPUB/PDF subjects with present, identified files;
  it reuses a fingerprint/extractor/limits cache and rebuilds author review. These
  contracts need a small extension for coherent title-and-author evidence, not a
  parallel full-book extraction pipeline.
- [ServiceCollectionExtensions](../../../src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs)
  already registers local inspectors/caches, Open Library and optional Google Books,
  the fixed mutation worker, and quarantine destination inspection.
- [Disposition tests](../../../tests/CalibreLibraryCleaner.Domain.Tests/Metadata/FinalRecordDispositionPolicyTests.cs)
  cover existing author outcomes, exact placeholders, weak typography, and ordinary
  labels. They do not yet establish complete retained-book identity coverage.
- Current safety/architecture documents describe verified cross-library copy before
  source deletion and shared-author reconciliation before quarantine review. Preserve
  these boundaries; implementation must inspect their existing tests before changing
  any contract they consume.
- Inspection found staged and unstaged implementation work already present. This
  plan neither stages nor modifies that work and makes no test-success claim for it.

## Proposed design

### 1. Qualification and final accounting

Use one deterministic, versioned disposition assessment per retained book, separate
from author suspicion and duplicate matching. Keep existing evidence values where
possible; add only the missing title, route-coverage, and disposition facts.

| Outcome | Meaning and next action |
| --- | --- |
| Qualified | Coherent usable title and credited author set, usable retained content, no decisive unresolved contradiction, and verified managed layout after approved writes. Evidence or explicit reviewer confirmation supports acceptance; absence of a regex hit is not verification. |
| Repairable | Evidence supports a concrete correction. Review/apply through existing metadata/final-author actions, normalize managed names, then reassess before qualification or transfer. |
| Needs review / incomplete | Roles or identity are ambiguous, evidence is insufficient, or applicable work is deferred/unavailable. Do not silently qualify or automatically select transfer merely because a provider failed. |
| Proposed Unresolvable | A material identity problem remains after applicable bounded routes completed, confirmed unusable content has no usable companion, or the user explicitly assigns the record here. Show evidence and require the existing confirmed mutation. |

These are analysis/review states, not additional repositories. Model author and
title confidence independently: a trusted author does not make a title trustworthy.
Optional missing cover, publisher, date, series, or ISBN does not fail qualification.
Anonymous/collective/corporate authorship and pseudonyms are legitimate when supported;
do not invent a personal author to make a record look complete.

Reassess against actual current metadata after corrections and shared-author
reconciliation. Superseded reasons must disappear. A review Skip/exclusion remains
available but does not certify a suspect record as high quality.

Freeze the retained-record count at the start of this final pass, after existing
duplicate cleanup. Successful final acceptance requires:

```text
retained input records = qualified records remaining + verified records moved
pending or unassessed records = 0
unresolved review exclusions in the claimed high-quality result = 0
```

Count new moves separately from records already in the destination. No record may
vanish from accounting or occur in two final outcomes. An incomplete run may leave
items in the source, but must not advertise that source as the finished high-quality
library. Failed/ambiguous mutation stops and requires Rescan; no failure recovery is
introduced to force the equation to hold.

### 2. General suspicion patterns, not authorship guesses

Apply cheap bounded checks to every title and complete author set. Use Unicode-aware
normalization, token/role comparisons, and bounded regular expressions where useful.
Version the rules and language vocabularies; give regexes explicit time/input bounds.
Illustrative detection families, not mandatory final regex implementations:

| Signal family | Example structural check | Guardrail |
| --- | --- | --- |
| Year/catalog prefix | `^\s*\(?[12]\d{3}\)?\s+\S` plus separate unmatched-delimiter detection | A year, life-date qualifier, or number is a warning, not proof of a book title. |
| Numbered volume | `^\s*\d{1,4}[.)-]?\s+\S` and whole-field numeric labels | Confirm title/series context; organizations and pen names can contain numbers. |
| Embedded series index | Author-field series/volume markers with an index, including text interleaved with a name | No novelist/character-name constants; distinguish normal initials and suffixes. |
| Placeholder/role label | Exact normalized missing-value vocabulary or collective/editor/translator labels | Field-sensitive: a literal title such as `Unknown` or a collective credit may be genuine. |
| Formatting damage | Unmatched delimiters, empty name components, repeated separators, stray boundary punctuation | Punctuation, initials, particles, apostrophes, and non-Latin scripts remain valid. |
| Author/title confusion | Author equals or strongly overlaps title/series; swapped title/author evidence across sources | Biography/self-titled works can legitimately share a person's name. |
| Probable truncation | Abrupt token endings or cut-off labels corroborated by full catalog/embedded/provider values | Calibre folder truncation alone must not condemn or reconstruct identity. |

No detector assigns an author from cultural familiarity, capitalization, script,
name length, or an allowlist. A malformed label can still identify a real author;
prefer correction over transfer. Original labels and reason codes remain reviewable.
Folder/file names are weak hints, never independent proof of identity.

### 3. Bounded local title-and-author evidence

Extend the current author inspection/cache boundary minimally to return paired
title, subtitle where present, ordered contributors/roles, relevant identifiers,
source location, coverage, and conflicts. Do not turn all retained prose into state.

- EPUB: use safe lazy package metadata and opening spine reading order; prioritize
  declared title/copyright/credits documents within the existing bounded budget.
  Skip blank/navigation-only units only with explicit accounting inside the budget;
  a cover or image-only unit is not negative authorship evidence.
- PDF: use the existing isolated worker and bounded first-page text/layout facts.
  Return reduced title/byline candidates with page/line or bounded layout provenance.
  Do not assume the first line or largest text is the title or author. Stay within
  the current first-five-page budget initially.
- Use existing role-aware byline logic plus small language-aware title/credit rules.
  Distinguish original author, translator, editor, illustrator, foreword writer,
  publisher, dedication, quotation, advertising, and references.
- Handle plain unlabelled title/byline pages as candidates with explicit uncertainty;
  an explicit `by` marker cannot be a prerequisite for all books.
- EPUB/PDF disagreement on a multi-format record is visible, not majority-voted away.
  Catalog values copied into embedded metadata are not independent corroboration.
- Missing first-page text, protected content, unsupported formats, parser limits,
  or missing bylines mean incomplete evidence, not proven corruption. Scan-like PDFs
  may still be readable and acceptable after review without OCR.
- Reuse existing assessment facts for readability. A low numeric score alone is not
  a transfer reason. A usable companion format prevents automatic whole-record
  quarantine for one bad format; unresolved companion defects must be disclosed and
  addressed through existing format-level capabilities or explicit review before
  claiming a clean final result.

Record route outcomes explicitly: not needed, not attempted, completed with evidence,
completed without usable evidence, conflicting, unavailable, and deferred by bounds.
Only applicable completed routes support an exhausted-resolution decision. An HTTP
429, missing API key, request cap, or parser timeout is not a completed negative.

### 4. Targeted bibliographic repair

Reuse existing Open Library and optional Google Books integrations and their budgets,
rate limiting, disclosures, failure isolation, immediate cache writes, and provenance.
Do not create a general internet-search agent or scrape arbitrary pages.

1. Prefer validated ISBNs associated with this publication; checksum validity alone
   cannot establish identity, and advertisement/reference ISBNs must be excluded.
2. Otherwise query a supported local title plus reliable author/language where known.
   For a clearly suspect author, permit bounded title-only or locally recovered-title
   retrieval; do not require the corrupt author to match the proposed correction.
3. Revalidate retrieval results against independent local evidence, complete credits,
   language, edition/work compatibility, and contradictions. Title-only retrieval or
   two providers repeating the same upstream catalog does not automatically confer
   high confidence. Never infer the author of a numbered title from a series rule.
4. Produce concrete field proposals using existing applicability/merge policies.
   Replacing populated title/authors remains explicitly reviewed; preserve unrelated
   identifiers and local fields. Never assemble incompatible edition facts.
5. Distinguish validated no-match, ambiguity, provider failure, and deferred demand.
   Preserve pending classification when only unavailable routes could resolve the
   issue; permit explicit user transfer without pretending resolution was exhausted.

For ordinary already-coherent records, reuse compatible evidence or obtain reviewer
confirmation rather than forcing an online lookup per book. Wider reading/search
and new providers require evidence that this bounded slice cannot solve the problem
and separate scope approval.

### 5. Review, mutation, and diagnostics

Extend existing review surfaces with only the missing title/author evidence,
disposition reason, coverage, and unresolved-count information. Support inspecting
the book with the existing viewer, reviewed correction, reviewed inclusion/exclusion,
and manual Unresolvable assignment. No new parallel dashboard or lifecycle UI.

Prepare read-only findings before mutation. Apply checked title changes through the
existing metadata-only action and checked authors through final-author mutation;
respect established non-author-before-author ordering. Refresh managed names, reread
shared-author side effects through the existing reconciliation, then rebuild the
current quarantine workspace. Do not require already-applied fields to be written
again. Use the existing operation gate and backup confirmations for each mutation.

Bind findings/selections to generation, revision, current metadata, complete format
identity, and relevant policy/extractor/provider versions. Changed evidence invalidates
stale decisions. Classification evidence never itself authorizes a worker command.

Reuse final quarantine's exact destination validation, copy-all-and-verify-before-
delete ordering, custom-field/cover/extra-file preservation, and stop-on-ambiguity
behavior. Do not add a new filesystem move or special author-folder transfer path.

The user permits detailed diagnostics. An explicit diagnostic run may record bounded
titles, author candidates, paths, source locations, and reduced lookup decisions
outside the libraries, while ordinary logging remains aggregate. Exclude credentials,
tokens, credential-bearing URLs, and full-book/provider dumps. Keep current PDF
worker no-page-text-retention rules; retaining new text excerpts would need an
explicit ADR amendment before implementation, not implicit permission in a logger.
Bound diagnostic volume and reuse the existing logging mechanism.

### 6. Caches, resource limits, and progress

Reuse existing caches rather than introducing a common cache framework. Local
evidence identity includes exact format fingerprint, format, parser/extractor,
language/role resources, and limits versions. Derived decisions additionally include
current title/ordered authors/identifiers, coverage, source/provider versions, and
disposition policy. Provider queries include their complete actual retrieval inputs.
Old author-only cache entries cannot prove newly added title extraction completed.

Cache loss/corruption is a miss. A cached limit, timeout, unavailable result, or
unfinished request remains visibly incomplete, not an invented negative finding.
Perform one indexed metadata pass, deduplicate unchanged format/provider demand, and
bound parser concurrency, result sizes, requests, and memory. No all-pairs comparison.

Report record coverage separately from completed parser units and actual HTTP
requests. Surface cached, inspected, no-match, unavailable, deferred, repairable,
qualified, proposed-transfer, moved, and pending counts plus phase/elapsed heartbeat.
Record cold/warm bytes, parser starts, actual requests, wall time, peak memory, and
log volume. Unchanged completed warm evidence must not trigger parser/network work;
explicitly expired/transient outcomes may require bounded new work.

## Files expected to change

Keep each implementation slice local; paths below are owners, not instructions to
edit every file. Prefer extending existing files over creating parallel abstractions.

- Domain: `src/CalibreLibraryCleaner.Domain/Metadata/AuthorSuspicionPolicy.cs`,
  `FinalRecordDispositionPolicy.cs`, and the existing byline/resolution values.
  Add a title/qualification value only if existing contracts cannot represent it.
- Application: `src/CalibreLibraryCleaner.Application/Metadata/` final-author and
  quarantine preparation, front-matter contracts/use case, metadata proposal/query
  orchestration; `Abstractions/IFrontMatterAuthorInspectors.cs` as needed.
- Infrastructure: existing `Epub/`, `Pdf/`, `Metadata/`, `Caches/`, and `Bibliographic/`
  adapters; extend the isolated PDF protocol/worker only for bounded reduced facts.
  DI changes only for genuinely new contracts.
- WPF: existing Final authors/metadata/Unresolvable rows and workflow gating under
  `src/CalibreLibraryCleaner.Wpf/`; Infrastructure references remain composition-only.
- Tests: reuse corresponding Domain Metadata, Application Metadata/Executions,
  Infrastructure parser/cache/provider/worker, WPF, and architecture test projects.
- Documentation: update this plan per slice; reconcile relevant current core docs,
  test strategy, roadmap, and any explicitly amended ADR before declaring completion.

## Safety considerations

- All diagnosis reads Calibre query-only and opens contained regular managed files
  read-only. Never modify a library during evidence extraction or a folder audit.
- Keep ZIP/XML/PDF/process/path bounds and external-resource prohibitions intact.
- Existing final backup prompts, fixed-worker-only mutation, destination UUID/schema
  validation, verified data preservation, and Rescan after ambiguity remain required.
- Do not infer identity from a truncated path, provider popularity, absent text, or
  cultural unfamiliarity. Preserve provenance and contradictions through review.
- Do not silently classify an uninspected record as qualified or a failed lookup as
  grounds to transfer. Manual assignment stays explicit and distinguishable.
- Never delete a record merely because its identity cannot be repaired. Existing
  quarantine may delete source records only after verified destination copies.
- Preserve existing generations/deltas/checkpoints without expanding recovery scope.

## Implementation steps

Each slice is independently reviewable. Add its failing focused check first, make
the smallest change, run that check immediately, and record actual results here.

1. **Freeze the qualification contract and regression fixtures.** Before substantial
   code edits, stage relevant current work as a rollback point without reverting
   anything, explain the chosen slice, and obtain explicit implementation approval.
   Inspect existing final-author/quarantine tests locally. Add generated cross-library
   cases and label each expected triage, repair, disposition, and route outcome.
   First discriminating checks: a missing final-author subject cannot by itself prove
   qualification; a bad title with a valid author must still receive assessment;
   incomplete evidence must not imply exhausted inspection. Do not change duplicate
   matching to make these checks pass.
2. **Extend general triage.** Implement bounded pattern families and field-sensitive
   reason codes with positive/negative multilingual tests. Cover year-prefixed and
   unmatched-year labels, numbered titles, embedded volume markers, true punctuation
   damage, and title/author swaps without adding recognizable author/series lists.
   Gate: all new patterns explain their hit and all legitimate-name counterexamples
   avoid unsupported automatic correction/transfer.
3. **Extract paired identity locally.** Extend existing bounded EPUB/PDF contracts,
   adapters, cache versions, and tests for title plus role-aware authors and coverage.
   Run EPUB and isolated PDF fixture checks separately. Gate: title-page fixtures
   recover coherent candidates with source locations; empty/image-only/conflicting
   pages remain explicitly unresolved; changed bytes/limits invalidate evidence.
4. **Resolve targeted repairs through existing providers.** Add only the missing
   suspect-author/title bootstrap query route and revalidation. Use fake HTTP fixtures
   for ISBN, recovered-title, title-only ambiguity, wrong language, multiple credits,
   copied metadata, outages, and request caps. Gate: every repair has concrete field
   provenance; provider unavailability cannot select transfer or qualify a record.
5. **Publish complete disposition review.** Integrate all retained records, title and
   author evidence, content usability, and explicit coverage into deterministic
   disposition. Reuse existing review/apply/include/exclude operations and current
   state binding. Gate: repaired records leave the proposal set after reassessment;
   unresolved/excluded/pending counts prevent a false high-quality completion claim;
   no new analysis path invokes mutation.
6. **Verify the two-library finish.** Reuse managed normalization, shared-author
   reconciliation, and confirmed quarantine unchanged wherever possible. Extend
   end-to-end generated-library acceptance with existing destination records, custom
   metadata, covers, multiple formats, and copy/delete failure cases. Gate: expected
   books remain/move exactly once, bytes and metadata are verified, output layout is
   Calibre-supported, and failed mutation still stops and requires Rescan.
7. **Calibrate, measure, and document.** Freeze a held-out evaluation independent of
   the example library; record metrics by signal family/language/format. Measure cold
   and warm behavior, run standard verification, then perform explicit disposable-copy
   acceptance through the packaged UI. Reconcile docs and archive this plan only
   after recording actual acceptance evidence and any remaining limitations.

## Tests

- Use xUnit, FakeItEasy, and FluentAssertions; generated/public-domain fixtures and
  fake providers by default. No personal library, live network, or embedded secrets
  in automated tests. Extend existing suites rather than proliferating test projects.
- Positive triage: arbitrary year-prefixed/truncated titles, unbalanced delimiters,
  numbered series items, generic volume labels, swapped fields, and missing identity.
- Hard negatives: non-Latin/transliterated names, initials, particles, apostrophes,
  hyphens, pseudonyms, life dates, corporate/collective/anonymous credits, biographies,
  numeric pen names, and genuine books whose titles resemble placeholders.
- Content: unlabelled title page; copyright/translation/editor credits; cover then
  blank/TOC/title pages within bounds; publisher ads/reference ISBNs; mixed-language
  pages; EPUB/PDF conflicts; scan-only/protected/limited/malformed/changed inputs;
  unsupported sole formats; bad PDF with usable EPUB. No unbounded fallback or OCR.
- Resolution: corrupted catalog author does not prevent independent ISBN/title
  repair; missing online match does not invalidate strong local evidence; provider
  disagreement, outage, cooldown, cap, or missing key cannot masquerade as exhaustion.
- Orchestration: one assessment per retained book including absent author subjects;
  evidence completeness independent of proposal availability; corrections reclassify;
  stale state/decisions invalidate; manual inclusion and Skip remain explicit.
- Mutation: no worker from analysis; backup confirmation; current source/destination
  binding; preservation of all supported metadata/files; all copies verified before
  any deletion; copy/delete failures stop; no fallback, rollback, or invented success.
- Cache/performance: complete identities, policy/resource changes, corrupt/old entries,
  deduplication, cold/warm work counts, true progress, and large synthetic review.
- Diagnostics: ordinary logs stay aggregate; enabled diagnostics remain bounded;
  credentials and prohibited PDF page text are never retained or logged.

Evaluation gates are targets, not measured results: 100% retained-record accounting;
at least 95% triage recall on labeled strong identity-damage holdout cases; zero
incorrect automatic replacement/default-transfer decisions on legitimate holdout
counterexamples; zero known bad records marked qualified in the holdout. Report
repair coverage, transfer precision/recall, review burden, and incomplete cases
separately. Split by work/source family to avoid fixture leakage; freeze labels and
thresholds before holdout evaluation. Do not infer safety from the example library
or from a high aggregate score that hides a language/format failure.

## Verification commands

Planning-only validation: check required sections, local Markdown links, and scoped
`git diff --check`. Builds, tests, performance, and real-library acceptance are not
part of creating this document and must not be reported as completed.

For implementation, inspect the repository test runner before choosing filtered
commands. Run the narrow changed policy/parser/use-case test first after every edit;
then run the repository's standard gates from the root:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Use the existing release build/test procedures for packaged acceptance. Record exact
commands, versions, corpus metrics, and failures in Progress. If the running cleaner
locks build output, stop and report its process details; do not use alternate output
directories or terminate the user's application. Tool-created workers may be stopped.

## Risks

- Heuristic bias can reject legitimate names; patterns route evidence/review rather
  than infer personal names or authorize writes.
- Local embedded and remote metadata can share the same wrong upstream source;
  provenance is needed to avoid false independent agreement.
- Bounded front matter can miss a valid title page, especially in image-only books;
  coverage must remain incomplete, with manual acceptance/transfer available.
- A strict final quality bar can leave many records pending. Expose the workload and
  calibrate evidence requirements without silently weakening the final accounting.
- Title-only search can produce convincing wrong editions; separate retrieval from
  identity validation and prohibit unreviewed populated-field replacements.
- Broader inspection can dominate cold time; deduplicate demands, reuse evidence, and
  measure costs before expanding reading/request limits.
- Calibre shared authors can affect unselected records. Reconcile and reassess after
  writes; do not reuse stale pre-write disposition evidence.
- Existing uncommitted quarantine work may evolve during implementation. Recheck the
  local boundary at each slice without reverting or rewriting unrelated changes.

## Unresolved questions

- No blocking question for this plan. The default product interpretation is
  trustworthy identity plus usable content, not mandatory rich metadata or a minimum
  keeper score. Final calibrated qualification thresholds are decided in slice 1
  and frozen before holdout evaluation.
- OCR, new providers/general web lookup, wider extraction budgets, and retained PDF
  text excerpts are separate decisions only if measured unresolved categories justify
  their cost; they are not prerequisites or implicitly approved dependencies.

## Progress

- [x] Read current planning instructions, relevant core documents, and accepted ADRs.
- [x] Inspect the disposition owner, quarantine review, author suspicion, front-matter
  contracts/orchestration, registrations, and neighboring disposition tests.
- [x] Identify the falsifiable coverage gap: absent author subjects/no suspicion do
  not establish whole-record title/author/content qualification.
- [x] Define bounded evidence, general patterns, repair-first review, two-library
  acceptance, implementation slices, and executable verification gates.
- [x] Planning validation passed: all 15 required sections, 23 local links across
  the plan/index, index registration, ASCII, trailing whitespace, and scoped
  `git diff --check`. No application tests were run for this documentation-only task.
- [x] Implementation requested on 2026-10-23. Clarification was attempted; the user
  was unavailable and requested pragmatic defaults. Use conservative qualification:
  independent coherent opening-page identity, usable content, and verified layout,
  or explicit reviewer acceptance. Missing evidence and unavailable routes stay pending.
  Preserve the existing Git index rather than stage other uncommitted work without
  explicit permission; this is a deliberate deviation from step 1's staged baseline.
- [x] Baseline: SDK 10.0.401, VSTest/xUnit 2; no running cleaner process detected.
  `dotnet test tests\CalibreLibraryCleaner.Application.Tests\CalibreLibraryCleaner.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~PrepareQuarantineReviewUseCaseTests" --verbosity minimal`
  passed all 5 tests.
- [x] Slices 1-2: explicit qualification/content/route contracts, bounded
  field-sensitive triage, unchanged duplicate matching, and multilingual/name
  counterexamples. The original two default-transfer regressions were reproduced
  failing before implementation and now pass.
- [x] Slice 3: reduced title/subtitle/byline pairs through the existing bounded
  EPUB/PDF boundary, cache schema `front-matter-identity-cache/2.0`, complete
  extractor/credit resource identity, and PDF protocol `1.1`. Headings alone and
  unlabelled pairs remain uncertain; declared title pages/explicit title labels
  supply stronger evidence. Advertising/reference contexts are excluded from paired
  identity. No PDF page text retention, OCR, rendering, larger limits or dependency
  changes were introduced. Real generated EPUB and isolated PDF fixture checks pass.
- [x] Slice 4: suspect-author title-only retrieval, ambiguity/Low-confidence gates,
  current-input binding, recovered local identity queries, and reviewed offline
  paired-identity repairs through existing proposal contracts. The local source is
  reduced evidence, not a new network provider. In the final repair pass, catalog
  ISBNs are not blindly trusted over contradictory local identity. Provider HTTP
  disclosure and zero-request offline repair tests pass.
- [x] Slice 5: all retained records appear in existing Unresolvable review.
  Qualification, provenance, route coverage, manual acceptance/transfer, repair,
  cache/parser/HTTP counts and frozen accounting are wired. Check all cannot assign
  untouched pending books. Non-author metadata repair precedes separately confirmed
  final authors, managed naming and shared-author reconciliation. Actual verified
  managed-name record IDs gate qualification; a successful run with skipped naming
  does not certify those skipped records.
- [x] Automated slice 6: 101-record/multiple-copy-chunk existing-destination tests,
  failure in the second copy chunk with zero deletions, first-delete failure with
  no retry/continuation, complete input identity and exact final accounting.
  The unchanged fixed-worker acceptance script ran against generated libraries
  with Calibre 9.x and returned `destination_created=true`,
  `destination_record_verified=true`, `duplicate_mode_used=true`,
  `source_deleted_after_copy=true`, protocol `1.4`. Its generated fixture is not a
  personal library or representative packaged UI acceptance.
- [x] Synthetic slice 7 measurements: the frozen independent 24-case identity
  holdout detected all 12 strong-damage cases (100% triage recall), with zero
  legitimate default transfers, zero known bad qualified records and zero automatic
  replacements. This is a small structural fixture, not broad multilingual book
  calibration. A 2,000-record fake-adapter pass with 20 distinct fingerprints used
  20 cold parser starts and zero warm starts/HTTP requests; all 2,000 warm demands
  hit compatible evidence. One measured run was 59 ms cold / 36 ms warm, with
  89,878,528 process peak working-set bytes. These are synthetic orchestration
  observations, not real-parser read-byte or throughput claims.
- [x] Failed checks were investigated, not bypassed: fixed PDF worker output needed
  rebuilding after its protocol changed; version-bound fixtures needed updating;
  formatting/import diagnostics were handled by the repository formatter. The
  architecture guard rejected metadata hashing and a presentation I/O namespace;
  identity is now an opaque structural in-memory value, the I/O reference was
  removed, and all 34 architecture tests passed without weakening a guard.
- [x] Initial Release publish with `--no-restore` failed `NETSDK1047` because
  `win-x64` assets were absent. The existing release builder then restored the
  missing RID assets and built a 79-file package. Final package verification and
  standard-suite results are recorded below after the final code freeze.
- [x] Final standard gates after code freeze:
  `dotnet build --no-restore --verbosity minimal` passed with zero warnings/errors;
  `dotnet test --no-build --maxcpucount:1 --verbosity minimal` passed all 1,177 tests
  (Domain 427, Application 294, Infrastructure 354, Architecture 34, WPF 68; no skips).
  `dotnet format --verify-no-changes --no-restore --verbosity minimal`,
  `git diff --check`, and `dotnet list package --vulnerable --include-transitive`
  passed; the package audit reported no vulnerable dependencies. Restore was
  performed successfully by the existing release builder after the missing-RID
  failure, not claimed as a separate manually run root command. Current-doc
  validation checked 38 local links. SDK was 10.0.401, test runtime 10.0.12 and
  actual fixed-worker acceptance runtime Calibre 9.12.
- [x] Final package build and extracted executable smoke passed using
  `.\scripts\Build-ReleasePackage.ps1 -Version 0.1.1-quality-20261023` and
  `.\scripts\Test-ReleasePackage.ps1 -ZipPath .\bin\releases\CalibreLibraryCleaner-0.1.1-quality-20261023-win-x64-framework-dependent.zip`.
  The ZIP contains 79 payload files, is 9,668,819 bytes, and has SHA-256
  `3e16843490661aafd09577cbaf157fa5da063c81a7c534e6e89138ac63373615`.
  This is package integrity/smoke verification, not representative destructive UI
  acceptance. Generated worker fixtures were removed.
- [x] Review follow-ups closed: author status/placeholder signals alone remain
  pending even through the legacy reason-assessment helper; heading-only identity
  is uncertain; offline paired repairs retain provenance; per-record verified
  naming IDs prevent qualification of skipped records; unsupported-format demand
  is aggregated/bounded; provider evidence is bound to complete current inputs;
  empty-format libraries can advance to read-only disposition without a fake write.
  No matching policy, fixed mutation worker, backup rule or architecture guard was
  relaxed. A concurrent baseline commit (`761ef04`) recorded the pre-existing
  staged quarantine work during implementation; this task did not stage, reset,
  revert or commit anything.
- [x] Remaining acceptance gates closed on 2026-10-24 using generated data only.
  Added process-scoped `CALIBRE_CLEANER_DATA_ROOT` and
  `CALIBRE_ONLINE_METADATA_ENABLED=0` for physically isolated offline acceptance;
  normal defaults are unchanged. Storage inside a library or through linked
  directories is rejected. No personal state, credentials or library was used.
- [x] Actual packaged WPF acceptance exposed and reproduced post-naming defects:
  filename extensions were being retained as catalog stems, fresh verified file
  observations were absent, native/canonical relative separators disagreed, and
  gate release did not refresh new WPF command bindings. Fixed the producers and
  notifications, not their symptoms. Typed worker protocol `1.6` now returns
  hash-bound current observations and registered names for naming and path-changing
  metadata writes. Existing delta kinds/persistence remain; no fallback engine,
  retry, recovery workflow or weaker authority was introduced.
- [x] Real parser holdout: 22 independent cases across EPUB/PDF and seven languages,
  including roles, unlabelled/undeclared pages, ads, scans, page-three/page-six bounds
  and conflicts. All labels matched, including after verified-name projection.
  Input was 23,324 bytes; cold 5,365 ms / warm 19 ms; cold parsers 22 (10 isolated
  PDF workers), warm parsers/workers zero, warm hits 22, HTTP requests zero.
  Parent process read-transfer bytes were 53,916 cold / 15,002 warm and peak
  working set 117,465,088 bytes. These I/O counters exclude PDF child-process I/O;
  they are not invented whole-system or source-only byte counts. Catalog and
  managed-format byte hashes remained unchanged by analysis.
- [x] Generated mixed-library WPF acceptance used 12 source records plus one existing
  destination record, custom values, covers, extra files, multiple formats,
  metadata-only records and malformed/image-only PDFs. Verified complete external
  fixture copies before each mutation and exercised actual confirmation dialogs.
  One author and one title correction were explicitly reviewed/applied; three
  legitimate retained records were explicitly accepted; four labeled records
  were explicitly assigned to transfer. Final UI accounting was
  `12 = 8 qualified remaining + 4 verified moved`, with zero pending and five
  destination records including the unchanged original record. Independent
  read-only Calibre verification proved exact title/credits/sorts, identifiers,
  publication/local/custom values, covers, extra files and format hashes, plus
  two-level record-ID-suffixed paths.
- [x] Repair coverage was 2/2 on the labeled repair cases; incorrect corrections and
  known-bad qualifications were zero. All four labeled transfers were verified
  (4/4 precision/recall with explicit review); automatic default transfers were
  zero, not misreported as automatic recall. Five records qualified from evidence
  after repairs, three required reviewer acceptance. UI restart loaded exactly
  eight authoritative source records and preserved the Rescan-before-next-mutation
  boundary. The acceptance parent peaked at 307,376,128 working-set bytes and
  recorded 9,428,899 read-transfer / 3,842,981 write-transfer bytes. Default logs
  totaled 62,629 bytes including restart, with zero generated bibliographic
  sentinel matches. All tool-created UI/worker processes exited.
- [x] Final build passed with zero warnings/errors; all 1,185 tests passed
  (Domain 429, Application 295, Infrastructure 358, Architecture 34, WPF 69),
  none skipped. Formatting, whitespace and package smoke passed. Final ZIP:
  9,674,950 bytes, 79 payload files, SHA-256
  `99b0e0e43c2fa47f79b1b7f6b0932c14fb7f16e50d330361695df8839c3b769b`.
  WPF end-to-end evidence used package SHA-256
  `581045128244cf13f8b07399369aa594183400a8f54433d715beaa2350955621`;
  the final refresh additionally bounds long subtitle transport while retaining
  complete evidence, with a new regression test and full-suite/smoke verification.
  Short-title acceptance behavior is unchanged. This is finite generated-fixture
  acceptance, not a universal accuracy claim for unseen personal libraries.

## Final outcome

The implementation, documentation, 1,185-test suite, real bounded-parser evaluation,
generated mixed-library packaged WPF acceptance, restart and final package smoke
are complete. The required accounting equation and exact preservation checks passed.
Pre-existing work was retained, and this task did not change the Git index or
create a commit. No personal library was read or mutated. Frozen duplicate matching
remain unchanged. The same fixed mutation worker has extended typed read-back;
its operation authority, confirmation and failure boundaries were not broadened.

The plan is complete and archived. No personal library was analyzed or certified;
unseen/ambiguous books still require the documented evidence or explicit review.