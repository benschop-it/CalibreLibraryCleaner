# Simplified Metadata Candidate Cleanup

## Objective

Replace the review-heavy Metadata candidates tab with the same keeper-oriented interaction used by Exact file duplicates, and add one worker-only batch command that consolidates every unskipped group.

## Scope

- Show normalized metadata groups with a session-scoped Skip checkbox.
- Show member records beneath the selected group with generated Keep/Remove actions.
- Select exactly one keeper by selecting a member row.
- Seed the keeper from the generated metadata source; groups without one use the first deterministic member.
- Keep the selected record's existing metadata unchanged.
- Transfer complementary formats selected by the generated recommendation.
- Resolve same-format conflicts in favor of the keeper and remove source alternatives.
- Remove all formats from non-keepers, then remove empty source records.
- Process all unskipped eligible groups in one confirmed worker-only run.
- Reuse projected-state durability, structured logging, uncertainty, checkpointing, and external-backup confirmation.

## Out of scope

- Copying metadata fields between records.
- Multiple keepers in one group.
- Persisting Skip/keeper choices across scans or loaded snapshots.
- Direct `calibredb`, SQLite, or managed-file mutation.
- Automatic retry or recovery after ambiguous mutation.
- Changing metadata grouping or recommendation ranking policy.

## Relevant requirements

- Exact normalized title/author matches are candidates, not proof of identical content.
- The user explicitly chooses whether to process each group and may change its generated keeper.
- Selecting Remove for a non-identical same-format source intentionally keeps the keeper's file.
- Every run requires confirmation that a complete external library backup exists.
- Mutation failures are logged, stop the run, and require explicit Rescan.

## Existing implementation inspected

- `MetadataDuplicateGroupRowViewModel` exposes generated recommendation, review overrides, format rows, reasons, warnings, and the unexplained `IsOverridden` flag.
- `MetadataDuplicateMemberRowViewModel` exposes `IsRetainedSeparate` rather than one keeper action.
- `MainWindow.xaml` presents review status, adjustment buttons, defer/filter controls, metadata source/format editors, reasons, and warnings.
- `ConsolidationRecommendation.MetadataSource.SelectedBookId` is the generated best metadata record.
- `FormatSourceSelection.ProposedSource` identifies a selected complementary format source when resolution is deterministic.
- `ExecuteBulkExactDuplicateCleanupUseCase` already owns the required lease, worker, run marker, complete-chunk projection, uncertainty, logging, and checkpoint pattern.

## Proposed design

Add a dedicated `ExecuteBulkMetadataCandidateCleanupUseCase` rather than weakening exact-binary planning rules. Its request contains one selection per unskipped group: group ID and keeper record ID. Planning validates the current snapshot and recommendation associations.

For each selected group:

1. reject stale/missing members and groups without a usable keeper;
2. require every non-keeper format to be present with a fingerprint;
3. retain every keeper format as-is;
4. for each format absent from the keeper, transfer the recommendation's selected present source to the keeper;
5. remove every format from every non-keeper, including non-identical same-format alternatives and non-selected complementary alternatives; and
6. remove each non-keeper record after all its formats are projected removed.

If a keeper lacks a format and the generated recommendation has no selected present source for it, the group is skipped before mutation. Group selections are disjoint and combined into one deterministic operation list. The use case executes through the same persistent worker protocol and state-session contract as exact cleanup.

WPF owns the transient Skip and keeper choices. Loading another scan/snapshot reconstructs rows and resets choices. The Metadata tab contains one Process button/status area, one group grid, and one member grid. Selecting a member changes the keeper; the Action column updates to Keep/Remove.

## Files expected to change

- `src/CalibreLibraryCleaner.Application/Executions/BulkMetadataCandidateCleanupContracts.cs`
- `src/CalibreLibraryCleaner.Application/Executions/ExecuteBulkMetadataCandidateCleanupUseCase.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateGroupRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateMemberRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataCandidateCleanupWorkspaceViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`
- Focused application, WPF, architecture, and persistence tests.
- Authoritative requirements, architecture, safety, roadmap, and test documentation.

## Safety considerations

- Non-identical same-format source files are intentionally removed in favor of the selected keeper; confirmation text must state this clearly.
- Never delete a source record unless all its formats are included in successful worker operations and projected deltas.
- Never transfer an unresolved, missing, or fingerprint-less complementary source.
- Do not partially project failed worker chunks.
- Do not log titles, authors, identifiers, or content.
- Loaded development state remains trusted by the developer under ADR 0016.

## Implementation steps

1. Add request/result contracts and focused planner/executor tests.
2. Implement deterministic metadata operation planning and worker execution.
3. Add keeper/Skip state and Action display to WPF rows.
4. Add the batch workspace, confirmation, progress, and terminal result.
5. Replace the Metadata candidates tab and remove obsolete review interaction code.
6. Update composition, close protection, tests, architecture rules, and documentation.
7. Run complete restore/build/test/format/diff/package verification.

## Tests

- Generated metadata source becomes the default keeper.
- No generated source keeps the first deterministic member and starts unskipped.
- Selecting another member changes exactly one Keep action.
- Skip excludes a group from the request.
- Complementary selected format transfers before source removals.
- Same-format conflicts are removed in favor of the keeper.
- Unresolved complementary source skips the group before mutation.
- Multiple groups produce one deterministic bounded worker run.
- Backup acknowledgement, worker startup failure, ambiguous chunks, logging, uncertainty, and checkpoint behavior match exact cleanup.
- Loading another snapshot resets Skip and keeper choices.
- XAML activation and close protection remain valid.

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

- Metadata groups can represent different editions despite normalized title/author equality.
- Choosing the keeper's non-identical same-format file is intentionally destructive.
- Recommendation format resolution may be absent for a complementary format; such groups must be skipped rather than guessed.
- Existing recommendation export/review code may become unused and should be removed only after the simplified UI compiles and tests pass.

## Unresolved questions

None. Ambiguous generated keeper groups use the first deterministic member and start unskipped.

## Progress

- [x] Existing metadata UI, recommendation model, exact cleanup worker, and tests inspected.
- [x] Product decisions recorded.
- [x] Application cleanup slice implemented and validated.
- [x] Simplified WPF interaction implemented and validated.
- [x] Obsolete review controls removed from the Metadata candidates tab.
- [x] Documentation aligned.
- [x] Complete verification passed.

## Final outcome

Implementation completed on 2026-08-08. The Metadata candidates tab now presents generated keeper/Remove actions, session-scoped Skip choices, and one batch Process candidates command. Application planning transfers selected complementary formats, deliberately removes non-identical same-format alternatives in favor of the keeper, removes all non-keeper formats and records, and skips unresolved groups before mutation.

`dotnet build --no-restore`, `dotnet test --no-build`, `dotnet format --verify-no-changes`, and `git diff --check` succeeded. The complete automated suite passed 456 tests with zero failures or skips. WPF XAML activation, generated/changed keeper actions, backup confirmation, complementary transfer, conflict removal, Skip behavior, and unresolved-source preflight behavior were covered. The direct/transitive package audit reported no known vulnerabilities.
