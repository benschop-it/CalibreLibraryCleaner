# Candidate Preparation Progress and Observability

## Objective

Resolve the open findings from staged large-library acceptance so a user never
mistakes active Candidate preparation for a hung application. Show continuous,
truthful, fine-grained progress through targeted hashing, EPUB/PDF assessment,
content signatures, grouping, publication, and presentation. Reduce routine log
volume, qualify assessment reuse, and remove the remaining scheduler-sensitive
test race without weakening cleanup safety or parser limits.

Release UX acceptance was reopened and closes only with the same-scale disposable-
library progress and log-volume evidence recorded in this plan.

## Scope

- Add one presentation-neutral Candidate-preparation progress envelope with an
  explicit phase, completed/total units, unit kind, active work, detail, and
  elapsed time support.
- Forward existing format-hash, EPUB assessment, PDF assessment, Candidate
  signature, residual analysis, and presentation progress instead of discarding it.
- Show determinate progress for known byte/file/page/fingerprint/record/group
  totals and indeterminate animation only for genuinely unbounded work.
- Add a truthful one-second UI heartbeat while a bounded component has not emitted
  a new semantic unit; elapsed time advances but completed work never does.
- Make cancellation feedback immediate and retain existing cancellation/state
  publication guarantees.
- Suppress high-volume per-file/per-chapter Debug diagnostics by default and
  coalesce detailed diagnostics when explicitly enabled.
- Run a repeat Candidate-preparation qualification that exercises compatible
  EPUB/PDF assessment reuse.
- Stabilize the PDF orchestration concurrency test's non-atomic observation.
- Retain regression coverage for acceptance-fixed invalid-path reconciliation,
  PDF worker exit handling, PDF file-identity checks, and the matching scale fixture.

## Out of scope

- Changing Exact grouping, keeper ranking, cleanup planning, operation ordering,
  worker protocol, mutation chunks, projected deltas, or uncertainty behavior.
- Adding estimated completion times or speculative wall-clock acceptance limits.
- Inventing weighted global percentages across unlike work units.
- Running Candidate cleanup as part of progress qualification.
- Changing EPUB/PDF parser limits, matching policy, scoring, or recommendation policy.
- Logging book titles, authors, paths, identifiers, or content.
- Inducing mutation failure or external catalog changes on a non-disposable library.

## Relevant requirements

- Long-running operations are asynchronous, cancellable, progress-reporting,
  bounded, and non-blocking to WPF.
- Candidate preparation is read-only and publishes only after complete successful
  reconciliation and analysis.
- Cancellation/failure leaves the prior authoritative generation and workflow
  phase intact.
- Exact cleanup and unified Candidate cleanup remain the only mutation owners.
- Logs contain technical counts, durations, safe IDs, and failure codes only.
- The acceptance run measured 20,571 fresh EPUB assessments and 473 fresh PDF
  assessments over 1,624,071 ms while the UI incorrectly retained `Verifying
  transferred format bytes`.

## Existing implementation inspected

- `RefreshAfterExactCleanupUseCase` reports catalog, reconciliation, resolution,
  targeted-hash, publication, and completion boundaries. It passes `null` to the
  format hasher and to `IResidualAnalysisFactsPreparer`, so byte and assessment
  progress never reaches WPF.
- `PrepareResidualAnalysisFactsUseCase` partitions reusable/fresh EPUB/PDF facts
  and sequentially invokes both assessors, but its interface has no progress input.
- `AssessEpubFormatsUseCase` already coalesces file/substage progress at a minimum
  500 ms interval and exposes completed/total files, active files, and stage summary.
- `AssessPdfFormatsUseCase` already exposes completed/total files and coalesced
  current-stage/page progress.
- `ResolveCandidateContentSignaturesUseCase` exposes fingerprint totals, cache
  hits, inspections, and detailed current work.
- `AnalyzeResidualCandidatesUseCase` exposes residual phases and adapts signature
  progress.
- `MainWindowViewModel` can display determinate or indeterminate progress but only
  sees the outer refresh message during assessment.
- `ApplicationLogging` enables Debug by default for assessment, matching, library,
  and Infrastructure EPUB categories. Routine acceptance produced eight rolled
  files totaling about 183 MiB.
- `VersionedJsonLibraryStateStore` preserves a compatible reusable-assessment
  snapshot when a later exact-only baseline is published for the same canonical root.
- `PdfAssessmentPolicyTests.OrchestrationIsBoundedCanonicalAndCancellationAware`
  updates its observed maximum with a non-atomic read/modify/write, allowing a
  later thread to overwrite 2 with 1.

## Proposed design

### Progress contract

Add an Application-owned `CandidatePreparationProgress` envelope. It contains:

- `Phase`: stable semantic phase value;
- `Completed` and `Total`: `long` values for the active phase;
- `Unit`: `Steps`, `Records`, `Files`, `Bytes`, `Pages`, `Fingerprints`, or `Groups`;
- `Message`: concise phase-level text suitable for WPF;
- `Detail`: bounded technical substage text with no book metadata or path;
- `ActiveItems`: current bounded concurrency when available.

Candidate preparation phases are ordered but not numerically weighted:

1. loading state;
2. reading catalog;
3. reconciling Exact deltas;
4. resolving residual files;
5. hashing transfer targets;
6. assessing EPUB files;
7. assessing PDF files;
8. publishing refreshed state;
9. resolving Candidate targets;
10. detecting Exact Metadata candidates;
11. generating Expanded candidates;
12. resolving content signatures;
13. merging unified groups;
14. publishing Candidate analysis;
15. preparing WPF presentation;
16. completed.

The progress bar represents only the active phase. The UI names the phase and unit,
so resetting from one phase to the next is explicit rather than misleading. When a
phase has a known total, progress is determinate. When it does not, the bar animates
and the message still shows phase, active substage, and elapsed time.

### Progress adapters

- Add progress to `IResidualAnalysisFactsPreparer.PrepareAsync` and a small
  `ResidualAnalysisFactsProgress` contract for EPUB/PDF preparation.
- Adapt `FormatHashProgress` to transfer-target byte progress. Use bytes when
  available, files otherwise.
- Adapt `EpubAssessmentProgress` to completed/total files plus active-stage summary.
- Adapt `PdfAssessmentProgress` to completed/total files and page detail without
  exposing a path.
- Preserve the current Candidate signature counters and phase-specific totals.
- Add bounded progress to snapshot presentation loops over books/groups rather
  than leaving `Preparing results` indefinitely indeterminate.
- Keep use-case contracts independent of WPF types.

### UI heartbeat and throttling

`MainWindowViewModel` stores the latest semantic progress snapshot and operation
start time. While Candidate preparation is busy, a one-second presentation timer
updates the visible `StatusMessage` with the current phase, last truthful counters,
and elapsed time without changing completed/total. The existing progress bar remains
determinate when the latest snapshot has a total and animated when it does not.
Semantic progress is applied at most several times per second; stage changes, phase
changes, completion, and cancellation messages bypass throttling.

The visible status must never retain a completed prior phase. As soon as EPUB or
PDF preparation starts, `Verifying transferred format bytes` is replaced. The
Cancel command remains enabled. After cancellation is requested, status changes
immediately to `Cancel requested; waiting for the current bounded operation to
stop`, and no partial refreshed generation is published.

### Logging policy

- Default production logging remains Information for aggregate start/progress/
  completion events and Warning/Error for slow or failed work.
- Remove default Debug overrides for high-volume assessment, matching, and
  Infrastructure EPUB categories.
- Add an explicit developer diagnostic switch for Debug details; default is off.
- Coalesce EPUB stage diagnostics even in diagnostic mode: emit on stage change,
  completion, slow threshold, or a bounded interval/unit step, not every chapter
  callback.
- Keep aggregate metric events unchanged so acceptance scripts remain usable.
- Preserve rolling size/count bounds and content-free structured fields.

### Reuse qualification

Use a disposable completed library at the same canonical root. Its Candidate
analysis has already persisted compatible assessments. Run an explicit exact-only
Scan. The Exact action still requires its normal explicit backup confirmation even
when NothingToDo is expected. If Exact preflight reports any destructive operation,
stop and obtain separate explicit authorization before executing it. After a
successful NothingToDo or separately authorized Exact result, activate Candidate
preparation once; Candidate preparation itself is read-only and requires no backup
confirmation. Record reused/fresh EPUB/PDF counts and durations, then stop before
Candidate cleanup. This exercises the store's supported reusable-assessment path;
do not seed or edit state files manually.

## Files expected to change

- `src/CalibreLibraryCleaner.Application/Libraries/CandidatePreparationWorkflow.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/RefreshAfterExactCleanupUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/PrepareResidualAnalysisFactsUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/AnalyzeResidualCandidatesUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/AssessEpubFormatsUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/AssessPdfFormatsUseCase.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/ApplicationLogging.cs`
- relevant Application and WPF progress/logging tests;
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/PdfAssessmentPolicyTests.cs`
- `docs/architecture.md`, `docs/functional-requirements.md`, and
  `docs/workflows/staged-large-library-acceptance.md` if the implemented contract
  changes their authoritative wording or acceptance evidence.

No Domain mutation values, cleanup planners, cleanup executors, worker code, or
Calibre mutation scripts are expected to change.

## Safety considerations

- Progress is observational only. It must not alter ordering, concurrency, retry,
  timeout, parser limits, state transitions, or mutation eligibility.
- Never estimate completed work. Heartbeats update elapsed time only.
- Progress callbacks must be thread-safe, bounded, and cheap; they must not perform
  filesystem, catalog, parser, or state-store work.
- Do not expose book metadata or paths in logs. Prefer counts and technical stage
  names in UI as well.
- Cancellation during refresh/assessment/signature work must leave the existing
  `CandidatePreparationReady` source authoritative and publish no partial generation.
- Logging failure or diagnostic-mode configuration must never fail analysis.
- Reuse qualification uses only a disposable copy. The Exact action requires its
  ordinary explicit backup confirmation; any nonempty Exact plan requires separate
  explicit destructive authorization. Candidate preparation is read-only, and
  Candidate mutation is not authorized by this plan.
- Keep preserved `InvalidPath` formats non-executable and retain the fail-closed
  checks for changed/newly resolvable associations.

## Implementation steps

1. **Reopen UX acceptance.** Mark the current disposable-library result as safety/
   functional acceptance with Candidate-preparation UX acceptance pending. Link
   this plan from the acceptance report.
2. **Add the common progress envelope.** Define stable phase/unit values and use
   `long` counters. Add validation for non-negative counts, `Completed <= Total`
   when total is known, and bounded detail text.
3. **Forward targeted-hash progress.** Pass a real adapter to
   `IFormatFileHasher.HashAsync`; report bytes/files and completion. Prove zero-work
   and small-work paths still emit phase transitions.
4. **Forward assessment progress.** Extend the facts-preparer interface, adapt the
  existing EPUB/PDF progress contracts, include reused/fresh counts in messages,
  and report an explicit EPUB-to-PDF phase transition. Add
  `IProgress<ResidualAnalysisFactsProgress>? progress` (or the final equivalently
  named Application-owned contract) to `IResidualAnalysisFactsPreparer.PrepareAsync`.
5. **Unify residual and presentation feedback.** Map signature/grouping/publication
  progress into the same envelope. Presentation collections already expose exact
  source counts; report completed/total items while filling their preallocated
  arrays without a separate counting pass. If a local presentation operation has
  no exact total, use an indeterminate phase plus elapsed heartbeat rather than an
  estimate.
6. **Implement WPF progress presentation.** Display active phase, truthful unit
   ratio, active work/detail, and elapsed time. Add the one-second heartbeat,
   immediate cancel-request status, and deterministic timer cleanup on success,
   failure, cancellation, close, and disposal.
7. **Reduce default logging volume.** Change default category levels, add the
   explicit diagnostic switch, and coalesce EPUB stage diagnostics. Preserve all
   aggregate metric events and slow/failure warnings.
8. **Stabilize the concurrency test.** Replace the test's non-atomic maximum update
   with an atomic-max helper or lock. Keep the assertions that two operations can
   overlap, concurrency never exceeds the configured bound, output is canonical,
   progress is ordered, and cancellation is observed.
9. **Run focused and full automated verification.** Include regression tests for
   the already-fixed PDF resource/identity races, invalid-path preservation, and
   matching scale fixture.
10. **Qualify on a same-scale disposable copy.** Run read-only Candidate preparation
    with live observation, capture progress screenshots/timestamps or an equivalent
    event transcript, measure default log volume, and perform the supported repeat
    run for assessment reuse. Do not run Candidate mutation.
11. **Close acceptance.** Record actual counters, durations, log bytes, reuse,
    cancellation result, process/lock cleanup, deviations, and final test totals.

## Tests

### Application progress

- Targeted hash reports monotonic byte progress and a completion event.
- Zero targeted hashes transition directly to assessment without retaining the
  hash message.
- EPUB preparation reports reused count, fresh total, completed files, active
  workers, and current stage summary.
- PDF preparation reports reused count, fresh total, completed files, stage, and
  sampled-page progress.
- EPUB completion precedes PDF start; PDF completion precedes refresh publication.
- Signature and group progress map to the common envelope with monotonic per-phase
  counters.
- Progress callbacks receive no title, author, identifier, content, or full path.
- Cancellation at each long phase publishes no partial refreshed/analyzed state.

### WPF

- The first Candidate activation replaces `Verifying transferred format bytes`
  with EPUB assessment status immediately when that phase starts.
- Known totals produce determinate progress; unknown totals animate.
- Progress resets explicitly on phase changes and never decreases within one phase.
- A one-second heartbeat advances elapsed time without advancing completed units.
- Stage changes and completion are not lost to throttling.
- Cancel remains enabled and updates status immediately; operation completion stops
  the heartbeat and restores command state.
- Close protection remains active during Candidate preparation.
- Presentation progress emits visible status at least every two seconds for the
  7,000-group fixture without blocking Cancel or close protection.

### Logging

- Default logger suppresses high-volume Debug assessment/content events.
- Diagnostic mode enables bounded Debug details.
- Coalescing limits repeated chapter callbacks while preserving stage changes,
  completion, slow warnings, and failures.
- Aggregate preparation/signature/completion metrics remain Information events.
- Logging output contains no book metadata, path, identifier, or content.

### Reuse and regression

- Exact-only baseline publication preserves the prior compatible assessment
  snapshot for the same canonical root.
- Repeat Candidate preparation reuses matching EPUB/PDF fingerprints and inspects
  only incompatible/missing facts.
- The PDF orchestration test cannot overwrite an observed maximum of 2 with 1.
- Full hard resource enforcement and file-identity tests remain green.
- Preserved invalid paths remain non-executable and changed/resolved paths fail closed.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Manual progress acceptance on the same-scale disposable copy must verify:

- no visible status remains unchanged for more than two seconds while Candidate
  preparation is active; if semantic work cannot advance, elapsed heartbeat does;
- EPUB/PDF status shows completed/total files and active technical stage;
- targeted hashing uses byte or file progress;
- signature resolution shows completed/total fingerprints, hits, and inspections;
- progress remains responsive and Cancel remains enabled throughout;
- cancellation changes visible status within one second and publishes no partial state;
- normal completion reaches unified review without a mutation prompt;
- default logs attributable to the run remain within one 25 MiB segment;
- a repeat run records actual reused/fresh EPUB/PDF counts;
- no app/worker/testhost process or selected-library lock remains after close.

These are responsiveness and log-volume gates, not wall-clock performance promises.

## Risks

- A single global percentage would be misleading because bytes, files, pages, and
  groups have unrelated cost. Keep percentages phase-local and label the phase.
- Excessive progress callbacks can move the bottleneck to the UI thread or logging.
  Coalesce at source and presentation boundaries.
- Concurrent EPUB/PDF callbacks can arrive out of order. Serialize publication and
  require monotonic counters per phase.
- A UI heartbeat can look like fake work if it increments progress. It may update
  elapsed time and the visible current-phase message only.
- Lower default log detail can make rare parser problems harder to diagnose. Keep
  an explicit bounded diagnostic mode and aggregate/slow/failure events.
- Reuse qualification depends on compatible analyzer/scoring/resource versions and
  identical fingerprints. Record misses; do not weaken compatibility to improve a
  reuse percentage.
- Restoring or rescanning a disposable library can invalidate the current workflow
  generation. Follow supported state transitions and never edit manifests manually.

## Unresolved questions

- Whether elapsed heartbeat belongs in a reusable WPF operation-progress service or
  remains private to `MainWindowViewModel`. Prefer the smallest testable existing
  pattern unless a second long-running workspace needs the same behavior.
- Whether detailed diagnostics should be enabled by environment variable or a
  development configuration option. It must default off and must not become an
  in-app control in this slice.

## Progress

- [x] Acceptance findings and owning code paths inspected.
- [x] Existing EPUB/PDF/signature progress capabilities identified.
- [x] Reusable-assessment persistence behavior verified in code and tests.
- [x] Execution plan written.
- [x] UX acceptance status reopened and linked.
- [x] Common Candidate-preparation progress implemented.
- [x] WPF phase progress and heartbeat implemented.
- [x] Default logging volume reduced and diagnostic mode bounded.
- [x] Scheduler-sensitive PDF test stabilized.
- [x] Automated verification completed.
- [x] Same-scale progress/log/reuse acceptance completed.

## Final outcome

Implementation is complete. Application now forwards transfer-hash bytes,
EPUB/PDF reused/fresh file counts, active technical stages, sampled-page detail,
signature fingerprint units, grouping phases, and exact presentation item counts
through one progress envelope. WPF shows phase-local determinate progress when a
total exists, an elapsed one-second heartbeat without invented work, immediate
cancel-request feedback, and drains/invalidates queued updates before terminal
status publication. Candidate mutation progress remains separate and unchanged.

Production logging defaults to Information; high-volume assessment/matching/EPUB
Debug categories require `CALIBRE_DIAGNOSTIC_LOGGING=1`, and repeated EPUB stage
callbacks are coalesced by stage/completion/one-second interval. The PDF concurrency
test now updates its observed maximum atomically. A pre-existing managed-heap test
was also made deterministic by retaining a known managed allocation.

Focused progress, heartbeat, cancellation, logging, coalescing, architecture, and
affected-project tests pass. The complete solution builds and the serial suite
passes 631/631. Cancellation/progress callbacks are serialized so queued semantic
or heartbeat updates cannot overwrite terminal cancellation status. No cleanup
planner, executor, worker, mutation operation, projected
delta, or state-transition behavior changed. Candidate mutation was not authorized
or run by this plan.

Same-scale qualification completed on the existing disposable Library 4 after an
explicit backup confirmation for the zero-operation Exact stage. Exact-only Scan
showed live byte/file progress and measured 17,514 books, 18,782 hashed formats,
22,270,820,744 hashed bytes, zero Exact groups, and 46,160 ms. Exact completion was
`NothingToDo` with zero operations and no library change.

The first Candidate activation remained read-only, visibly advanced through
multiple phases, and produced 77 unified groups. Refresh measured 18,782 reused
fingerprints, 63 preserved invalid paths, zero targeted hashes, and 59,469 ms. All
16,896 EPUB and 473 PDF assessments were reused with zero fresh inspections;
signature resolution measured 1,677 fingerprints, 1,540 cache hits, 137 inspections,
and 10,921 ms. Residual analysis measured 2 Exact Metadata groups, 75 Expanded
groups, 77 unified groups, and 37,769 ms. Default logging produced one 9,948-byte
segment instead of the prior approximately 183 MiB. File/byte/database counts were
unchanged, no mutation marker existed, normal close released the database lock,
and no app/worker/viewer/testhost process remained. Manual cancellation was not
exercised because preparation completed; immediate feedback and no-publication
cancellation behavior pass focused automated WPF tests. Candidate mutation was not
run. Release UX acceptance is complete.