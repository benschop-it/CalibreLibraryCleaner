# Align Expanded evidence summaries with the staged workflow

## Objective

Stop directing users to rescan when Expanded evidence is intentionally absent
before Candidate preparation or after completed Candidate cleanup.

## Scope

Phase-aware presentation for unavailable/stale Expanded evidence using the current
authoritative checkpoint and displayed snapshot. Preserve genuine rescan warnings.

## Out of scope

Matching algorithms, cached evidence, mutation planning/authority, library state
projection, retaining obsolete groups, new workflow actions or library changes.

## Relevant requirements

- [Functional requirements](../../functional-requirements.md): Exact-only scan,
  read-only Candidate preparation and later Candidate mutation.
- [ADR 0020](../../adr/0020-staged-exact-first-candidate-cleanup.md): staged workflow.
- [Architecture](../../architecture.md): obsolete pre-mutation evidence cannot remain
  executable for the projected retained library.

## Existing implementation inspected

`ApplySnapshot` always tells users to run a fresh scan for Unavailable/Stale
Expanded summaries. Exact-only snapshots have not run Expanded discovery yet, and
mutation projection deliberately discards pre-cleanup matching groups/summary.
The running UI confirmed Candidate cleanup completed with Prepare online metadata
review enabled, while the Expanded tab still instructed a fresh scan.

## Proposed design

Use stage-specific guidance only for the exact displayed authoritative snapshot
with a current compatible checkpoint. ExactReady points to Exact cleanup;
CandidatePreparationReady points to Prepare Candidate review; completed cleanup
points to metadata review; Completed points to final review. Legacy, uncertain,
stale-checkpoint and unexpectedly incomplete Candidate review retain rescan advice.

## Files expected to change

Existing MainWindow view-model presentation, its WPF regression tests and directly
related workflow documentation.

## Safety considerations

No library access beyond existing application behavior. Missing execution evidence
is not fabricated, old groups are not retained, and no cleanup precondition changes.
Do not interrupt the user's running workflow or bypass normal build locks.

## Implementation steps

1. Reproduce incorrect guidance on a fresh staged scan and completed cleanup state.
2. Add phase-aware summary text without changing evidence or commands.
3. Test both missing/stale states, valid available evidence and genuine rescan cases.
4. Verify normal build/tests when outputs are unlocked and archive after completion.

## Tests

- Fresh Exact scan directs Candidate preparation, not another scan.
- ExactReady directs Exact cleanup.
- Completed Candidate cleanup directs metadata review.
- Final stage directs final review.
- Uncertain/incompatible checkpoints and missing expected Candidate evidence still
  instruct Rescan; legacy guidance and valid evidence counts remain unchanged.

## Verification commands

Filtered MainWindow WPF tests, then standard solution build/tests/format/whitespace.
No alternate output configuration or user-process termination for locked builds.

## Risks

Consulting a checkpoint for a different snapshot could conceal genuine missing
evidence. Require exact snapshot identity and authoritative compatible state.

## Unresolved questions

None. The reported message is a stale presentation instruction, not a cleanup failure.

## Progress

- [x] Traced the message and confirmed the live workflow completed without modifying UI.
- [x] User cleaner PID 60868 remains running; requested closure before rebuilding.
- [x] Reproduced nine failing WPF checks before production changes: eight
  phase/evidence combinations and an actual fresh Exact-only scan all gave the
  same misleading rescan instruction.
- [x] Phase-aware summary implemented using the exact displayed snapshot and a
  current authoritative checkpoint. Available evidence, matching data, mutation
  checks and workflow actions are unchanged.
- [x] Added negative checks for missing expected Candidate evidence, uncertain
  state, incompatible checkpoints and a different displayed snapshot.
  Formatting/static diagnostics and the production diff are checked below.
- [x] Normal updated WPF test build was initially blocked by the running user app:
  MSB3021 could not replace its normal Debug `CalibreLibraryCleaner.Wpf.exe`
  (cleaner PID 60868). The attempt used `CopyRetryCount=0` to report the lock
  promptly, not an alternate output/configuration. At that point source compiled
  but updated tests did not execute; those attempts were not reported as passing.
  Requested closure through the clarification tool; user unavailable. No process
  or live review was changed. The user app subsequently closed, releasing outputs.
- [x] Normal Debug solution build passed with zero warnings/errors using
  `dotnet build --no-restore --no-incremental --verbosity minimal`.
  Full `dotnet test --no-build --maxcpucount:1 --verbosity minimal` passed
  all 1,290 tests: Domain 478, Application 320, Infrastructure 368,
  Architecture 34, WPF 90; none skipped. The 90 WPF tests include the stage-specific
  unavailable/stale guidance, actual fresh scan, all four trust guards, legacy
  rescan warning and valid evidence presentation cases.
- [x] Formatting verification, whitespace checks, source review and plan links
  passed. A subsequent normal incremental build passed and the dependency audit
  reported no vulnerable packages. Documentation reconciled and plan archived without matching, mutation,
  library state, authority or command changes.

## Final outcome

Corrected, rebuilt and verified in normal Visual Studio outputs. Completed
Candidate cleanup points to online metadata review, not a redundant scan; genuine
missing/untrusted evidence still warns. Read-only inspection confirmed the
reported cleanup had completed successfully. No library or live review state was
changed. The updated application is ready for F5.
