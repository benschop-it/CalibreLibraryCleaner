# One-Click Exact Duplicate Cleanup

## Objective

Make the Exact file duplicates tab a complete workflow with generated keepers, user overrides, and one bulk command that removes duplicate formats, merges unambiguous records, and deletes empty records.

## Scope

- Editable keeper selection for every eligible exact group.
- One bulk command over all current selections.
- Automatic temporary staging for complementary formats.
- Typed `add_format`, `remove_format`, and non-permanent `remove` commands.
- Durable state deltas and progress/result presentation.
- Hidden technical plan, execution, and recovery tabs.

## Out of scope

- Overwriting non-identical same-format conflicts.
- Automatically merging a source with multiple possible keeper records.
- Direct database or managed-library filesystem writes.
- A startup disclaimer; it may be added separately.

## Relevant requirements

- Stay close to the product goal and avoid exposing internal workflow stages.
- Honor generated and manually selected keepers.
- Process thousands of groups without rescans.
- Use supported typed Calibre commands only.

## Existing implementation inspected

- The exact tab exposed six internal controls and two acknowledgement checkboxes.
- Exact cleanup executed only one group at a time and did not transfer complementary formats.
- The persistent state session already supports add/replace, format removal, and record removal deltas.

## Proposed design

Build one deterministic operation list from all current group selections. Preserve every selected keeper. Remove every non-retained exact copy. For a non-keeper source with remaining formats, merge it only when all duplicate relationships point to one target and no same-format conflict exists. Stage all required source files before mutation, add them to targets, remove source formats, then remove empty records.

## Files expected to change

- Application bulk workflow contracts and use case.
- Infrastructure transfer staging.
- Exact duplicate row and workspace ViewModels.
- Main window bindings and composition.
- Focused Application/WPF tests and active documentation.

## Safety considerations

- No direct SQLite or managed-file writes.
- Staged transfer bytes must match the scanned fingerprint.
- Add completes before its source format is removed.
- Conflicting and ambiguous records remain untouched.
- Command ambiguity marks projected state uncertain.

## Implementation steps

1. Add editable keeper overrides.
2. Implement global operation planning.
3. Add automatic transfer staging.
4. Execute adds, removals, and empty-record deletion with deltas.
5. Replace the staged UI with one command.
6. Hide technical tabs and verify the complete workflow.

## Tests

- Algorithmic default keeper is shown.
- Clicking another member changes the keeper.
- One command processes all selections.
- Complementary formats move before source deletion.
- Same-format conflicts remain and are reported.
- Empty records are removed once.
- No plan, backup-folder, preparation, approval, or checkbox controls remain visible.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check HEAD
```

## Risks

- Overlapping groups can create multiple merge targets; those records are intentionally skipped.
- Calibre command failures can leave partial work and therefore mark state uncertain.

## Unresolved questions

- None for this slice.

## Progress

- [x] Editable keeper overrides implemented.
- [x] Bulk duplicate removal and unambiguous merge workflow implemented.
- [x] Automatic transfer staging implemented.
- [x] Exact-tab UI simplified.
- [x] Technical tabs hidden.
- [x] Full verification and diff review complete.

## Final outcome

The Exact file duplicates tab now shows generated keepers, lets row selection override each group, and exposes one `Remove duplicates` command. The bulk workflow processes all current selections, automatically stages and transfers complementary formats for unambiguous records, removes duplicate/source formats through typed Calibre commands, and deletes empty records. Conflicting or multi-target records remain and are reported.

The per-group plan, validation, approval, backup-folder, preparation, and acknowledgement controls were removed from the tab. Technical cleanup-plan, execution, and recovery tabs are hidden from normal use. Focused Application, Infrastructure, and WPF tests cover command ordering, merging, conflicts, staging, keeper overrides, and one-button execution.