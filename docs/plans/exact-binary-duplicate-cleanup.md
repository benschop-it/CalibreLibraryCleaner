# Exact Binary Duplicate Record Cleanup

## Objective

Let a user select one keeper in an exact-binary duplicate group and remove every other Calibre record in that group through `calibredb` after a verified external backup.

## Scope

- Use row selection as the single keeper control; show every other record as a derived deletion.
- Model exact-binary cleanup independently from metadata consolidation.
- Preserve the keeper and all unrelated records.
- Back up complete non-keeper records, including metadata, covers, unique formats, and managed state.
- Generate, validate, approve, back up, execute, journal, and verify every non-keeper record removal.
- Revalidate group membership, file identity, hashes, paths, library identity, and approval immediately before mutation.
- Invalidate the persisted library snapshot before mutation so the next application run requires a fresh scan.
- Use only the typed non-permanent `calibredb remove` boundary for removal.

## Out of scope

- Inferring that records with different metadata are duplicate books.
- Choosing or merging metadata for exact-binary groups.
- Keeping multiple records from the selected exact-binary group.
- Automatically selecting a retained file.
- Enabling an unqualified real-Calibre compatibility profile.
- Bulk execution of multiple groups in one approval.

## Relevant requirements

- Exact binary equality is file-level evidence and does not imply record equivalence.
- Destructive work requires an immutable explicitly approved plan, complete verified external backup, live revalidation, supported Calibre tooling, post-command scans, audit history, and recovery information.
- No direct SQLite or Calibre-managed filesystem writes are permitted.
- AI recommendations cannot authorize mutation.

## Existing implementation inspected

- `MainWindow.xaml` originally presented exact-binary groups read-only and provided no review action.
- `CleanupPlanDefinition` is specifically a metadata-consolidation model: one target record, one metadata source, one retained source per canonical format, and removal of every non-target record.
- `CleanupPlanSafetyPolicy` requires recommendation-driven whole-group consolidation and cannot represent a user-selected subset of exact-group records.
- Milestone 7 already provides typed non-permanent record `remove`, which the exact-record executor can reuse.
- Infrastructure already has a closed typed non-permanent `remove` mapping used by cleanup execution.
- The exact Calibre 9.11.0 cleanup profile is enabled by default with runtime identity/version/help probes.

## Proposed design

Introduce an exact-binary cleanup aggregate rather than weakening metadata-consolidation invariants. Its immutable body contains the library and exact-group identity, the retained exact-file association, every non-keeper record ID, complete expected states for all involved records, exact shared fingerprint evidence, backup requirements, and explicit review provenance.

The plan lifecycle follows the existing immutable draft/valid/approved/stale/revoked pattern. Approval binds to a canonical digest. A fresh snapshot makes a plan stale when the keeper, any removal, path, format, fingerprint, record, library identity, or group membership changes.

Execution uses a separate compact operation graph whose only mutations are non-keeper record removals. Before mutation it exports and verifies every involved record and copies every managed format to an external bundle. Each operation invokes typed non-permanent `calibredb remove`, then performs a complete read-only scan proving that the deleted record disappeared, the keeper remains unchanged, and unrelated records are unchanged.

The Exact file duplicates tab uses row selection as the keeper choice and displays a read-only `Keep` or `Delete book` action for every row.

## Files expected to change

- Domain exact-binary cleanup plan and execution values/policies.
- Application plan generation, validation, approval, preparation, and execution use cases.
- Infrastructure plan artifact storage, backup/audit support, and typed `remove` mapping.
- WPF exact-duplicate selection, plan review, confirmation, and execution presentation.
- Domain, Application, Infrastructure, WPF, and architecture tests.
- Product, functional, domain, safety, architecture, roadmap, and ADR documentation where the supported model changes.

## Safety considerations

- Remove every distinct non-keeper record that currently contributes a member to the selected exact-binary group.
- Require one non-null keeper record at all times.
- Back up every format, metadata export, cover, and managed state for every involved record before mutation.
- Fail closed on missing files, stale hashes, path changes, covers that cannot be preserved by the backup contract, unsupported Calibre versions/capabilities, concurrent mutation, or incomplete verification.
- Keep exact executable/version/capability checks at every command gate.
- Never invoke `remove` through a shell or arbitrary argument path.

## Implementation steps

1. Add exact-binary selection state and focused WPF behavior tests.
2. Add immutable exact-binary plan values, canonical digest, safety validation, lifecycle, and tests.
3. Add generation/validation/approval use cases and snapshot-staleness tests.
4. Persist the approved plan in the verified external execution bundle.
5. Add exact-binary preparation, complete record backup, typed `remove`, audit, verification, and failure/cancellation tests.
6. Wire plan/execution workspaces and confirmations without changing metadata-candidate behavior.
7. Update authoritative documentation and ADRs.
8. Run focused tests after each slice, then the standard repository verification commands and complete-diff review.

## Tests

- Keeper must be one current member of the exact-binary group.
- The group must span at least two distinct records.
- Every non-keeper record must contribute current exact-file evidence matching the keeper fingerprint.
- Metadata differences do not block single-keeper consolidation.
- Unique formats, metadata, and covers on non-keeper records are complete backup expectations; unrelated records are preservation expectations.
- Changed/missing/inaccessible files and stale scans block approval/execution.
- Tampered plan bodies, approvals, backup manifests, and journals are rejected.
- `remove` arguments are closed, direct, and exact-version gated.
- Cancellation before mutation prevents mutation; cancellation after mutation requests a verified safe stop.
- A failed or ambiguous post-command scan stops later deletions and identifies the verified bundle/full copy as the recovery source.
- Existing metadata consolidation tests remain unchanged and passing.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

## Risks

- Adding a second immutable plan shape can increase UI and persistence complexity; keep its types separate rather than adding nullable consolidation fields.
- A non-keeper record may contain additional unique formats; the UI states this and the executor backs them up before deleting the record.
- Recovery can use the complete exported/raw record bundle; automated restore remains optional because users may restore their full library copy.
- The real-Calibre profile must probe and qualify non-permanent `remove` for this exact operation.

## Unresolved questions

- Whether a later bulk workflow should approve several exact groups in one immutable plan.

## Progress

- [x] Inspected current exact-duplicate UI, cleanup-plan invariants, execution boundary, and governing ADRs.
- [x] Implement row-selected keeper and derived all-other-record deletion.
- [x] Adapt the plan lifecycle, validation, and approval to exactly one survivor.
- [x] Persist the approved plan in the verified execution bundle.
- [x] Implement backed-up typed execution and verification.
- [x] Complete review/approval UI wiring and current-scope documentation.
- [x] Run full verification and review the diff.

## Final outcome

Explicit duplicate-record deletion is implemented through typed non-permanent `calibredb remove`, backed by complete involved-record exports, raw format copies, a sealed manifest, append-only audit, and full scans after every deletion.
