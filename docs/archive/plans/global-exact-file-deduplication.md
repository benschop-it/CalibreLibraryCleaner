# Global Exact-File Deduplication

## Objective

Replace manual per-group whole-record deletion with deterministic scan-wide retained-copy decisions and immutable format-cleanup plan slices that remove other copies and remove a Calibre record only when no formats remain.

## Scope

- Evaluate every exact-binary group from one completed fresh snapshot with one deterministic policy.
- Skip anomalous groups whose members have different canonical format labels.
- Retain one copy using, in order: most formats expected to remain on the record, metadata/identifier/cover completeness, and lowest Calibre record ID.
- Represent format removals and derived empty-record removals in an immutable approved plan.
- Back up affected records and formats, revalidate immediately before each mutation, use typed `calibredb remove_format` and `calibredb remove`, and verify each result.
- Present the generated retained copy in Exact file duplicates while preserving manual inspection.

## Out of scope

- Moving complementary formats between records.
- Fuzzy or identifier-based duplicate-book discovery.
- Merging remaining records after exact copies are removed.
- Automatically resolving non-identical files of the same format.
- Removing existing recovery safeguards or direct-Calibre mutation restrictions.

## Relevant requirements

- Never write directly to `metadata.db` or Calibre-managed files.
- Never delete a unique format without a verified backup.
- Destructive actions require an immutable, explicitly approved, freshly revalidated plan.
- AI recommendations never authorize mutation.
- The normal workflow must not require thousands of manual keeper choices.

## Existing implementation inspected

- `ExactBinaryDuplicateDetector` produces deterministic fingerprint groups ordered by record ID.
- `ExactDuplicateGroupRowViewModel` currently treats the first member as the keeper.
- `ExactBinaryCleanupPlan` currently requires one keeper record and deletion of every other group record.
- Exact-binary execution currently backs up complete records and invokes typed non-permanent `calibredb remove`.
- The Calibre gateway already has a capability-gated typed `remove_format` operation for recovery.

## Proposed design

Add versioned scan-wide exact-file retention decisions in Domain. Each eligible group contributes one retained format association and one or more duplicate-format removals. A bounded immutable plan materializes one eligible group decision for approval and execution. Record removals are derived only when all formats on that record are in the plan's format-removal set. Groups with mixed canonical format labels are recorded as skipped anomalies.

Selection is deterministic and simulated across the complete proposal. A candidate first ranks by the number of formats expected to remain on its record, then by a named metadata-completeness vector (usable title, usable authors, valid recognized identifiers, cover, publication metadata), then by lowest record ID. The plan records the evidence used for each choice.

Application generates, validates, approves, prepares, and executes each bounded plan slice from the scan-wide decisions. A slice becomes stale after affected state changes and must be regenerated from a fresh scan. Infrastructure exposes `remove_format` through the execution gateway using the existing direct-process and capability boundary. The executor preserves the current external backup, lease, audit, cancellation, fresh-scan, and post-command verification rules.

## Files expected to change

- Domain exact-binary selection and cleanup-plan values/policies.
- Application plan, execution, and Calibre gateway contracts.
- Infrastructure typed Calibre command implementation and backup/audit support.
- WPF exact-duplicate presentation and cleanup workflow.
- Domain, Application, Infrastructure, WPF, and architecture tests.
- ADR 0010, a superseding ADR, and authoritative product documentation.

## Safety considerations

- A format removal is valid only when another current member has the same canonical format label, length, and SHA-256.
- Mixed-label groups are skipped rather than normalized into eligibility.
- Complete affected-record backup and sealed-manifest verification remain mandatory before mutation.
- Every mutation is preceded by a fresh read-only scan and followed by semantic verification.
- A record removal is permitted only after a fresh scan proves it has no remaining formats.
- Unexpected group, path, hash, metadata, or record-state changes fail closed.

## Implementation steps

1. Supersede the single-keeper-record ADR and update authoritative requirements.
2. Add and test deterministic global retained-format selection.
3. Replace the exact-binary plan body with format removals and derived empty-record removals.
4. Promote typed `remove_format` to the execution boundary and adapt backup, audit, and verification.
5. Replace manual first-row retention with generated selection in WPF.
6. Run focused tests after each slice, then repository-wide verification and diff review.

## Tests

- A later record wins when it has more remaining formats.
- Metadata/identifier/cover completeness breaks equal-format-count ties.
- Lowest Calibre ID is only the final tie-breaker.
- Mixed-format-label exact groups are skipped.
- Overlapping groups are resolved deterministically using simulated remaining formats.
- Format removal never removes a record that still contains another format.
- A record is removed only after its final format is removed and verified absent.
- Stale fingerprints, paths, labels, records, backups, approvals, and tool capabilities block execution.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check HEAD
```

## Risks

- The current exact-binary aggregate and WPF workflow assume one group and one surviving record; replacing that contract touches every layer.
- Overlapping groups make selection order observable unless the simulation and canonical ordering are explicit.
- Removing the last format and then removing an empty record requires two separately verified mutations.

## Unresolved questions

- None for this slice. Phase 2 record merging remains a separate future plan.

## Progress

- [x] Confirmed the regression and recovered the agreed ranking and deletion semantics.
- [x] Inspected the current plan, executor, command gateway, and presentation boundaries.
- [x] Supersede conflicting ADR and requirements.
- [x] Implement and test scan-wide retained-format selection.
- [x] Implement and test format-level planning and execution.
- [x] Update WPF presentation and workflow.
- [x] Complete repository verification and diff review.

## Final outcome

ADR 0011 supersedes the rejected single-keeper-record workflow. Exact groups now receive deterministic generated retained-copy decisions using format count, metadata completeness, validated identifiers, cover presence, and record ID only as the final tie-breaker. Mixed canonical format labels are skipped.

The v2 exact-binary plan removes duplicate formats through typed, capability-probed `calibredb remove_format`; it can list a record for non-permanent removal only when the complete expected format set is removed. Execution verifies backup, state, tool identity, format absence, empty-record state, record absence, and unrelated-state preservation around every command. WPF presents generated actions and row selection no longer changes the retained copy.

Phase 2 transfer and merge behavior remains outside this plan.