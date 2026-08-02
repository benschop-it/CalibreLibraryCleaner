# Persistent Delta-Driven Library State

## Objective

Perform one explicit full scan for a library, then persist and apply typed state deltas for every successful cleanup and recovery mutation without any further full or targeted library scans.

## Scope

- Add an authoritative/uncertain versioned library-state aggregate.
- Add typed deltas for every supported cleanup and recovery mutation.
- Incrementally update books, formats, duplicate groups, findings, assessments, and recommendations.
- Persist a baseline/checkpoint, hash-chained delta journal, and atomic state manifest across restarts.
- Remove all execution, preparation, verification, failure, and recovery scan calls.
- Make explicit Scan the only way to incorporate external changes or clear uncertainty.

## Out of scope

- Automatic external-change detection.
- Full-scan fallback after command ambiguity.
- Direct SQLite or Calibre-managed filesystem mutation.
- Replacing existing backup, export, lease, approval, or typed-command boundaries.
- Phase 2 duplicate-record proposal logic beyond using the common delta infrastructure.

## Relevant requirements

- Bulk cleanup must scale to thousands of operations.
- Successful typed commands are authoritative for their intended effects.
- Failed or ambiguous commands stop immediately and block mutation until explicit rescan.
- Projected state persists across restart.
- Existing architecture dependency direction remains unchanged.

## Existing implementation inspected

- `ScanLibraryUseCase` performs catalog reading, file resolution, hashing, ebook assessment, duplicate grouping, and recommendation generation.
- Exact cleanup scans before and after every format and record removal.
- Metadata cleanup and recovery also scan at command gates, phase boundaries, failure handling, and final verification.
- `LibrarySnapshot` is immutable and contains books plus fully derived analysis collections.
- `ILibrarySnapshotStore` persists one latest `library-snapshot/1.0` JSON artifact.
- Recovery record creation already carries an optional `CreatedRecordId`; the new contract must require it on success.

## Proposed design

`LibraryState` wraps one materialized `LibrarySnapshot` with a generation ID, monotonically increasing revision, authoritative or uncertain status, baseline scan time, last projected time, and uncertainty evidence. A closed `LibraryStateDelta` hierarchy records command intent and expected effect.

The pure Domain projector validates the expected revision and operation preconditions, applies one delta to immutable book state, invalidates affected scan-only evidence, and deterministically rebuilds affected derived state. The first implementation slice supports exact duplicate format removal and derived record removal. Later slices add format add/replace, recovery record creation, and metadata changes with explicit projected-fact provenance.

Application owns one `ILibraryStateSession` per canonical root. Infrastructure persists an immutable checkpoint, append-only hash-chained delta journal, and atomic manifest. WPF consumes published revisions and treats authoritative persisted state as mutation-eligible.

## Files expected to change

- ADRs and authoritative architecture, requirements, safety, and domain documentation.
- Domain library-state, delta, projection, and projected-provenance values.
- Application state-session and persistence ports plus every execution/recovery use case.
- Infrastructure state-generation serialization, journaling, replay, and compaction.
- WPF scan/load/status and workspace revision handling.
- Domain, Application, Infrastructure, WPF, performance, and architecture tests.

## Safety considerations

- This design intentionally trusts successful typed command results instead of rereading Calibre.
- Deltas use optimistic revision checks to prevent applying against the wrong internal state.
- Any command or persistence ambiguity marks state uncertain and blocks all mutation.
- Backups and exports remain required by their existing plans.
- External changes are not observed until explicit rescan.

## Implementation steps

1. Add ADR 0012 and align authoritative documentation.
2. Add `LibraryState`, status/revision values, uncertainty evidence, removal deltas, and a pure projector.
3. Add projected physical-fact provenance and the remaining mutation delta kinds.
4. Add incremental duplicate/recommendation indexes and affected-evidence invalidation.
5. Add baseline/checkpoint, delta-journal, manifest, replay, and compaction persistence.
6. Add the shared Application state session.
7. Migrate exact cleanup and remove its scans.
8. Migrate metadata cleanup and remove its scans.
9. Migrate recovery, require deterministic created IDs, and remove its scans.
10. Update WPF to load authoritative projected state and block uncertain state.
11. Add large synthetic performance and zero-rescan tests.
12. Complete repository verification and diff review.

## Tests

- Revision conflicts and uncertain state reject deltas.
- Format and record removal update only affected records and exact groups.
- Changed formats lose stale assessments/findings; unaffected evidence is preserved.
- Every supported command maps to exactly one replayable delta.
- Torn intent, missing commit, persistence failure, and missing created ID become uncertain.
- Restart replay and compaction produce identical materialized state.
- Explicit scan resets generation and clears uncertainty.
- A 10,000-record, 6,000-delta workflow invokes one scanner and no post-baseline readers, hashers, or inspectors.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check HEAD
```

## Risks

- Projected state can diverge from Calibre after external changes.
- Existing domain values assume scan-observed paths and IDs; projected provenance must avoid fabricated values.
- Recovery record creation depends on stable parsing of Calibre's created-record ID.
- Persisting one delta per command must remain cheaper than repeated whole-snapshot serialization.

## Unresolved questions

- None. State lifetime, workflow scope, external-change behavior, and command-failure behavior are fixed by ADR 0012.

## Progress

- [x] Architecture and mutation workflows inspected.
- [x] Performance and consistency decisions fixed.
- [x] ADR 0012 accepted.
- [x] Authoritative state and removal delta foundation implemented.
- [ ] Remaining projected mutation types implemented.
- [ ] Persistence and replay implemented.
- [ ] Cleanup and recovery scan calls removed. Exact-file cleanup is complete; metadata cleanup and recovery remain.
- [ ] WPF lifecycle migrated.
- [ ] Performance proof and final verification complete.

## Final outcome

In progress.