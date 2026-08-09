# Composite Reviewed Cleanup

## Objective

Allow one explicit scan, review of Exact file duplicates, Metadata candidates, and Expanded candidates, and one `Cleanup all` command that validates all reviewed selections together, reports cross-workflow conflicts before mutation, and executes one deterministic worker-only operation graph without rescanning between categories.

## Scope

- Capture current keeper/Skip selections from all three views.
- Build every category plan from the same authoritative scan generation and revision.
- Detect incompatible cross-category record and format intentions before tool discovery or mutation.
- Show blocking conflicts in a modal dialog with category, group, record, and format identifiers.
- Permit compatible overlaps and deduplicate identical operations.
- Execute one sequence: constructive transfers, format removals, then empty-record removals.
- Use one external-backup confirmation, one mutation lease, one persistent `calibre-debug` worker, one mutation marker, typed chunks, durable projected deltas, and one final checkpoint.
- Invalidate inferred matching evidence after mutation without requiring an intermediate scan.

## Out of scope

- Automatic resolution of conflicting keeper choices.
- Metadata title/author rewriting.
- Running category-specific cleanup commands as part of `Cleanup all`.
- Reanalysis, hashing, or content inspection during cleanup.
- Continuing after a blocking conflict or ambiguous worker result.

## Relevant requirements

- Never write directly to `metadata.db` or Calibre-managed files.
- Exact, metadata, and expanded selections are explicit user review state.
- A complete external backup must be confirmed per mutation run.
- Only the fixed persistent worker may mutate the library.
- Complete successful chunks project typed deltas; ambiguous outcomes mark state uncertain.
- Cleanup must not trigger scans.

## Existing implementation inspected

- Each category currently builds and executes its own plan against current authoritative state.
- Exact cleanup can transfer complementary formats and remove exact copies/empty records.
- Metadata cleanup transfers uniquely selected complementary formats, then removes non-keeper records.
- Expanded cleanup uses content-confirmed groups and a quality-selected/user-overridden keeper.
- Any mutation clears recommendations and inferred groups in projected state, which prevents chaining Expanded cleanup after another category without rescan.
- WPF already holds all three reviewed selection sets before mutation.

## Proposed design

### Composite request

One request contains:

- initial library root, generation, and revision;
- exact retained-member selections;
- metadata group keeper/Skip selections;
- expanded group keeper/Skip selections; and
- external-backup confirmation.

### Category planning

Run the three pure planners against the same immutable authoritative snapshot. A selected unskipped group that a category planner would skip for incomplete/stale/ambiguous evidence becomes a blocking composite conflict rather than a silent skip.

### Conflict model

Build normalized intentions:

- `Transfer(source record, target record, format, fingerprint)`;
- `RemoveFormat(record, format, expected fingerprint)`;
- `RemoveRecord(record)`;
- explicit keeper records and exact retained format associations.

Block when:

- one source format transfers to different targets;
- one target format receives different fingerprints;
- a selected keeper is removed by another workflow;
- an exact retained association is removed by another workflow;
- a surviving record format is both retained and removed;
- a record is removed while also serving as a transfer target;
- duplicate category plans disagree about the target keeper for overlapping records; or
- a selected group cannot produce a complete safe category plan.

Identical transfers/removals are deduplicated. Compatible overlap, such as exact cleanup retaining the same record selected as metadata keeper, is allowed.

### Execution order

1. Transfer every complementary format to its final target.
2. Remove duplicate/source formats.
3. Remove records proven empty by the combined simulated final inventory.

The composite planner simulates inventory from the initial snapshot and verifies every record removal is empty after all planned format changes. Operation IDs are category-neutral and canonical.

### UI

Add one top-level `Cleanup all` command. It is enabled only for authoritative state and at least one reviewed eligible selection. On invocation:

1. build/validate the composite plan without mutation;
2. if conflicts exist, show a modal list and stop;
3. user changes keeper/Skip selections and retries;
4. if conflict-free, request one external-backup confirmation containing operation/category counts; and
5. execute with aggregate progress and terminal results.

Category-specific buttons may remain for focused workflows, but `Cleanup all` is the only workflow that guarantees all initial-scan selections can be applied without an intermediate rescan.

## Files expected to change

- New Application composite contracts, planner, and executor.
- Existing category planners exposed internally as pure planning inputs.
- WPF cleanup-all workspace, conflict dialog service, composition registration, command/button, and close protection.
- Domain/Application/WPF/architecture tests.
- Safety, functional requirements, architecture, and README documentation.

## Safety considerations

- Conflict detection finishes before tool discovery, lease acquisition, worker startup, or mutation marker publication.
- No conflict is auto-resolved.
- The plan binds to one authoritative generation/revision and is rebuilt on every button press.
- No initial-scan physical path is fabricated after transfer; projected state is updated only from successful typed operations.
- Any unexpected execution failure follows existing uncertain-state behavior.

## Implementation steps

1. Add composite request/result/conflict/operation values.
2. Expose category planning outputs without widening mutation APIs.
3. Implement normalized intention merge, simulation, and conflict detection.
4. Implement one-worker composite execution and projected delta publication.
5. Add WPF selection aggregation, conflict modal, confirmation, progress, and button.
6. Add overlap/conflict, operation-order, failure, state, and UI tests.
7. Run full restore/build/test/format/diff/vulnerability verification.

## Tests

- Disjoint exact/metadata/expanded selections merge into one ordered operation graph.
- Compatible overlapping keeper choices are accepted and duplicate operations deduplicate.
- Different keepers for overlapping groups block before worker startup.
- Exact retained format scheduled for removal blocks.
- Transfer-target and target-fingerprint conflicts block.
- Category preflight skips become blocking composite conflicts.
- Transfers precede removals; record removals are last and only for simulated-empty records.
- One backup confirmation, lease, worker session, marker, chunk stream, and final checkpoint are used.
- Worker failure marks state uncertain and stops.
- Conflict modal contains safe actionable identifiers and no book content in logs.
- Selection changes clear conflicts on retry.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

## Risks

- Category planners currently contain overlapping but not identical transfer policies; normalization must not weaken their safety checks.
- Exact groups are file-level while metadata/expanded groups are record-level, so compatible overlap requires inventory simulation rather than category ordering alone.
- Large libraries can produce many operations; chunking remains bounded at 100.
- The running WPF process may lock final desktop validation outputs.

## Unresolved questions

- Whether category-specific destructive buttons should later be hidden in favor of the composite command. Initial implementation retains them.

## Progress

- [x] Existing category planners, projected-state behavior, and WPF selections inspected.
- [x] Composite conflict and execution-order contract documented.
- [x] Composite planner and conflict values implemented.
- [x] Unified worker execution implemented.
- [x] Cleanup-all UI and conflict modal implemented.
- [x] Complete WPF/architecture validation performed after the running desktop process released build outputs.

## Final outcome

The composite planner merges all three reviewed selection sets from one authoritative scan, allows compatible overlap, blocks incompatible keeper/transfer/removal intentions, simulates final inventory, and emits one canonical transfer-then-format-removal-then-record-removal graph. One executor uses one backup confirmation, lease, worker, mutation marker, bounded chunk stream, projected delta stream, and checkpoint. WPF exposes Cleanup all, exact/metadata/expanded Skip and keeper choices, aggregate progress, and a bounded conflict modal. The complete solution builds; all 546 tests pass; formatting/diff checks pass; and the package vulnerability audit is clean.
