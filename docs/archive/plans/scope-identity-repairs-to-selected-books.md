# Scope identity repairs to selected books

## Objective

Make Find repairs act only on selected Unresolvable rows, never silently search
other suspect records, and show the actual record scope in progress and results.
Make explicit quarantine selection/movement independent of optional identity
repair and avoid another library-wide inspection just to show the move list.

## Scope

Explicit validated record IDs through WPF, Application demand planning and review
subject publication. Preserve caches, unrelated saved decisions and unselected
quarantine choices/evidence. Preserve every checked and explicitly unchecked
quarantine move during read-only repair and Metadata Apply checkbox changes.
Keep every repair proposal initially unchecked.
Separate metadata-application and quarantine-move commands so preparing/checking
repairs cannot change a move action into a metadata update. Report the actual
mutation phase rather than calling managed naming "unified candidates."
Build quarantine rows from current metadata/existing evidence without opening
every retained format. Inspect opening pages only through explicit selected repair.

## Out of scope

Changing provider matching/query policies, arbitrary request limits, cache
invalidation, mutation behavior, background retry or searching the user's library
to test the fix.

## Relevant requirements

- Current user request: selecting record 467 must not cause library-wide HTTP work.
- [Architecture](../../architecture.md): Application owns query scope and review ports.
- [Safety](../../safety-and-rollback.md): read-only lookup is not mutation authority.
- [ADR 0021](../../adr/0021-matching-first-performance-oriented-product.md): bounded
  work, warm reuse and truthful progress.

## Existing implementation inspected

WPF's repair command passes the full state and identity workspace without selected
IDs. Application scans every retained book for triage/identity differences, queries
that demand and builds review rows for the full library. Progress does not mention
the selection. Technical log confirms the library-level repair run began at 22:22
and accumulated many HTTP requests. It does not show that they belonged to one book.

Saved decision writes currently replace the file with the active workspace's
overrides. Scoped review must preserve decisions for other subjects. Workspace
selection copies also lose provider result provenance; keep it through selection.

## Proposed design

Label the button Find repairs for selected and disable it without a current row.
Capture selected IDs once, falling back only to the current active row. Validate
nonempty, distinct current IDs before cache/provider calls. Filter all local
identity extraction, provider demand and output subjects by these IDs; selected
books are eligible even if their review-only warning is not an older triage reason.
Normal post-cleanup metadata review remains whole-library.

Progress/status names a single record ID or selected-record count, and reports
actual completed queries/cache/HTTP counts. Multiple bounded provider queries for
one book may remain necessary; do not imply one book equals one HTTP request.
Publish only scoped repair rows, unchecked, in Metadata review. Reassessment
updates selected records without resetting unselected quarantine choices or evidence.
Merge saved decisions by workspace subject ID instead of deleting unrelated ones.
Quarantine-list preparation performs no new document or provider inspection.
Optional local repair inspection uses the same validated selected IDs and merges
new observations with existing unselected observations. Manual selected transfers
do not require automatic title/byline qualification of the entire retained library;
keep missing certification in optional accounting without bypassing any selected
copy/read-back/delete or external-backup precondition.

## Files expected to change

Preparation/subject-building/reassessment use cases, WPF command/XAML,
Application/WPF regressions and current docs.

## Safety considerations

Reject stale/empty/duplicate scope before I/O, never silently widen to the library.
No repair automatically mutates or checks Apply. Provider cache identities,
budgets, failure handling and mutation confirmation are unchanged.

## Implementation steps

1. Reproduce unrelated provider work/review rows using a generated multi-book fixture.
2. Wire strict selected scope end-to-end and preserve decisions/provenance.
3. Keep quarantine reassessment isolated and make scope visible in the UI.
4. Verify cold/warm selected requests, invalid scope, normal full review and build.

## Tests

One selected record among many suspect records; multiple selected records; missing,
duplicate and empty IDs fail before I/O. Provider/cache queries and local repairs
reference only selected IDs; no unselected review rows. Compatible warm repeat has
zero provider requests. Unrelated persisted decisions and quarantine selections
survive; repair output remains unchecked. Actual WPF command captures the selection
and reports scope; no selection disables it.

## Verification commands

Focused Application/WPF tests, normal Debug build, full solution suite,
format/whitespace and dependency vulnerability audit. Respect running app locks.

## Risks

Partial workspaces can delete unrelated decisions or reset earlier review choices
if old whole-library assumptions remain. Validate selection and test those surfaces
explicitly. Do not promise constant latency for external provider response times.
The user subsequently reported that the existing whole-library repair rebuild
cleared a large manually checked move list. The same scope/reassessment fix must
cover hundreds of checked records, deliberate exclusions, the repaired book itself,
repeat repair searches and checking Metadata proposals without applying mutation.

## Unresolved questions

None. The selected rows define scope; no hidden all-library fallback is acceptable.

## Progress

- [x] Confirmed selected row is not passed to the existing repair workflow.
- [x] Added mandatory explicit repair scope and selected-only local/provider
  demand plus review publication. Normal metadata preparation stays full-library.
- [x] Added explicit button/progress scope, no-selection gating and initially
  unchecked results. Preserved provider provenance, unrelated saved decisions and
  unselected quarantine choices/evidence.
- [x] Initial full Application suite passed (339); generated 1,000-record tests
  proved one/two selected records cause only those cold requests and warm repeats
  cause zero provider requests. Added local-only and failure-path checks afterward.
- [x] Final full Application suite passed (341), including selected local repairs,
  unchecked results on decision-read failure and unselected quarantine preservation.
- [x] Repository formatting verification, whitespace and dependency vulnerability
  report passed.
- [x] Added 1,000-record preservation regressions with hundreds of checked moves,
  explicit exclusions and repeat reassessment. WPF coverage also checks metadata
  proposal selection and view changes without changing the move list.
- [x] Preserve a previously checked policy suggestion when new read-only evidence
  changes its classification; retain the checkbox as a reviewed choice rather than
  claiming obsolete automatic policy authority.
- [x] All 45 focused Application metadata/quarantine checks passed, including the
  large-choice cases and changed-classification selection preservation.
- [x] Full Application suite passed (344) after the selection-loss fix and
  large-choice regressions. Formatting verification passed again.
- [x] Actual WPF command/window verification passed after voluntary app closure,
  including the 289-move/seven-repair routing and preservation regressions.
- [x] Normal Debug build and full solution verification completed on 2026-10-29:
  Domain 517, Application 357, Infrastructure 391, Architecture 34, WPF 108;
  all 1,407 passed, zero skipped. Formatting, whitespace and dependency audit passed.
- [x] Reconciled current documentation and archived the completed workflow fixes.

## Final outcome

Completed and verified. The normal F5 program contains selected-only repairs,
preserved move choices during read-only work, independent move/metadata actions
and no mandatory whole-library document inspection merely to show quarantine rows.
The user voluntarily closed the application; it was never terminated by the agent.

A late full-suite run exposed an in-flight scan progress callback racing terminal
error reporting. Progress delivery and completion now share the same gate; the
read-only failure tests and complete WPF/full solution suite passed afterward.
The test logging collector is also thread-safe.

Existing quarantined destination records were not modified. The subsequent
quarantine failure left the source state uncertain, so next startup requires
Scan rather than Load; these UI changes do not authorize a retry into an existing
destination or reconstruct choices already discarded by the old program.

### Historical blockers and intermediate evidence

The following records why runtime verification was initially delayed. All pending
verification references below were resolved by the final result above.

Implementation and Application verification complete; normal WPF build/test
verification is blocked by the user's running application (observed PID 30748).
Closure was requested but no user response was available. No process was terminated
and no alternate output/configuration was used to bypass locks. Do not claim that
the running program or normal F5 binaries contain the fix yet.
The user reported existing selections had already been discarded. No durable
quarantine choice store exists in that version, so do not claim those lost choices
can be reconstructed. Do not terminate the active app or advise rebuilding a large
manual choice list in the still-unfixed running version. The bounded wait-for-close
verification helper was stopped when this report arrived, so no build can start
unexpectedly while work is being edited.

Next action after normal application/debugger closure: run focused WPF tests
(`FindRepairsPublishesOnlySelectedRecordsAndReportsTheirScope`, existing quarantine
selection/read-only error tests and actual MainWindow tests), then normal Debug
build, full solution tests and final documentation reconciliation/archive.

Further live-session findings:

- The user has 289 checked quarantine moves and explicitly requested no termination.
  Leave the running app and those choices untouched; do not ask for restart while
  the active session is needed.
- The old shared action prioritizes identity metadata whenever repair review is
  open, so it asks to apply seven metadata proposals instead of moving 289 records.
  Added distinct source commands/buttons with independent counts and confirmation
  paths; the main quarantine action no longer gives repair flags precedence.
- Existing-version workaround was verified read-only in source: decline metadata
  confirmation, Clear all Apply in Metadata review, then invoke the zero-repair
  action once to return to disposition without changing quarantine choices.
- Live technical events subsequently showed a 16-operation metadata update completed
  at 22:50:35, followed by 17,160 naming operations at 22:50:51. The application-owned
  journal's latest kind was `refresh-managed-name`, with an active mutation marker.
  Do not interrupt or advise closure during that mutation. The generic Candidate
  progress text was misleading; source now names actual metadata/author/naming work
  and states that it is not a quarantine move.
- All 108 focused execution/metadata/quarantine Application checks passed after
  those progress updates. WPF command separation and 289-versus-seven regression
  coverage remain pending normal output access.
- The updated WPF source was type-checked without replacing any running binary by
  using the normal `PrepareForBuild;ResolveReferences;PrepareResources;Compile`
  targets with project-reference builds disabled. The initial incomplete Compile/
  markup-only target combinations lacked generated entry-point/XAML references;
  the complete normal reference/resource pipeline succeeded. This is compilation
  evidence only, not a claim that runtime WPF tests or the installed F5 output
  passed. The 289-versus-seven test now independently exercises both confirmation
  services and asserts that declining either never clears move choices.
- The user then reported the eager `Inspecting retained identity` phase after
  preparing quarantine. Removed that whole-library inspection from selection-list
  preparation. Optional Find repairs now inspects only its selected IDs, retaining
  unselected observations. Added one-selected-among-1,000 inspection/cache tests
  and a 289-selected-among-1,000 move test proving exactly 578 copy/delete operations
  and no metadata/naming operations. Updated actual WPF command coverage to require
  zero document-cache/provider calls when merely preparing the move list.
- Full Application suite passed (346), normal intermediate WPF compilation and
  formatting verification passed after those changes. The active application was
  not interrupted or replaced. Runtime WPF tests and final normal output copying
  remain blocked until the user voluntarily ends the active session.
