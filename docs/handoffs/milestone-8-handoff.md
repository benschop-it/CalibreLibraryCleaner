# Milestone 8 Verified Rollback and Recovery Handoff

## Purpose and baseline

This handoff is the starting point for work after Milestone 8. Read it together
with:

1. `AGENTS.md`, `PLANS.md`, and every nested `AGENTS.md`;
2. `docs/plans/milestone-8-verified-rollback-and-recovery.md`;
3. `docs/adr/0008-reconciliation-based-verified-recovery.md`;
4. `docs/architecture.md`, `docs/domain-model.md`,
   `docs/safety-and-rollback.md`, `docs/test-strategy.md`, and
   `docs/roadmap.md`; and
5. `docs/handoffs/milestone-7-handoff.md`.

Runtime is .NET 10 and Windows WPF. Milestone 8 was implemented on
2026-07-24 and its safety-critical review remediation completed on 2026-07-25.
No Milestone 9 work was included.

## Safety-review remediation

The completed implementation was reopened for a safety-critical review and the
findings were corrected with fail-closed V1 behavior:

- every executable semantic plan field, reconciliation fact, expected state,
  preservation rule, source-chain identity, and destructive operation is
  canonical-hash bound; approval and execution confirmation bind the immutable
  artifact revision and body;
- a selected source must have the exact approved bundle identity and a verified
  plan, legal hash-chained journal, original manifest, and complete immutable
  backup artifacts. Nonterminal source executions do not require a fabricated
  summary, while an orphan summary without its journal terminal event is
  ignored;
- current-state backup inventory and OPF metadata are semantically
  cross-validated, every entry and both manifest digests are rehashed, and a
  durable guard plus a matching post-backup fresh scan precede mutation;
- affected metadata, unexpected formats, preservation expectations, unrelated
  state, exact destructive targets, and both backup generations are checked at
  command gates and final verification;
- unsupported/manual operations cannot be relabeled into dispatchable phases.
  V1 blocks destructive record deletion and permits destructive format removal
  only for an exact approved semantic record and format fingerprint;
- created Calibre IDs are never assumed. A fresh scan delta identifies the
  actual record and the journal cannot accept `Recovered` until a finalized
  semantic mapping agrees with the approved formats and identifiers;
- mutation process cancellation never kills Calibre, partial work is never
  automatically retried/resumed/reversed, window close is blocked while either
  mutation workflow is busy, and terminal/journal gaps remain unresolved; and
- both real-Calibre harnesses require an exact caller-supplied executable and
  caller-marked operating-system temporary root, reject reparse/substituted
  paths, and never discover a user library.

Post-remediation verification completed with 408 passed, zero failed, and two
skipped opt-in real-Calibre tests. The focused recovery suite completed with 76
passed and one skipped opt-in qualification. Build had zero warnings and zero
errors; formatting and `git diff --check` were clean. Normal restore still
reproduces the machine-level `NU1507` nine-source error; an ephemeral
nuget.org-only config restored successfully and was removed.

## Implementation completed

Milestone 8 now implements one explicitly selected Milestone 7 execution at a
time as a fail-closed, reconciliation-driven recovery workflow:

- strict reloading and digest verification of the source cleanup plan,
  hash-chained execution journal, any journal-proven terminal summary, backup
  manifest, and every required original backup artifact;
- compatibility validation for the Milestone 7 `1.0.0` and actual assembly
  `1.0.0.0` stamps, source schemas, exact tool identity, and recovery capability
  profile;
- a complete fresh read-only scan and deterministic three-way reconciliation
  of pre-state, durable source progress, and actual current state;
- immutable `cleanup-recovery-plan/1.0` bodies with canonical digests, versioned
  artifact revisions, approval, revocation, warning acknowledgements, and
  staleness bindings;
- a separate create-only current-state recovery bundle containing the approved
  recovery plan, source-chain audit copies, complete affected-record export and
  inventory, all current formats and covers, hashes, and a reverified manifest;
- one shared library-mutation lease domain for cleanup and recovery;
- typed Calibre recovery mappings through the existing direct
  `ProcessStartInfo.ArgumentList` runner, never through a shell;
- constructive recovery, operation-level semantic scans, intermediate
  verification, a separate exact destructive confirmation, destructive
  recovery last, and final semantic verification;
- durable logical-to-Calibre record-ID mappings when recreated records receive
  new numeric IDs;
- hash-chained recovery journals, immutable terminal summaries, append-only
  recovery history, and create-only source-resolution links;
- safe-boundary cancellation that never terminates a mutating Calibre process,
  automatic retries a mutation, resumes a partial recovery, or attempts a
  rollback of recovery;
- accurate `Recovered`, `PartiallyRecovered`, `VerificationFailed`, and
  `ManualInterventionRequired` outcomes; and
- a WPF Recovery tab for one source bundle, reconciliation review, individual
  warning acknowledgement, approval, backup status and progress, separate
  destructive confirmation, cancellation explanation, final result, and ID
  mappings.

The original Milestone 7 bundle is opened read-only and is never rewritten.
Production code does not write to `metadata.db` or mutate Calibre-managed paths
with filesystem APIs. Every library mutation is a typed supported-tool command.

## Files changed

### Documentation

- `docs/adr/0008-reconciliation-based-verified-recovery.md`
- `docs/architecture.md`
- `docs/domain-model.md`
- `docs/plans/milestone-8-verified-rollback-and-recovery.md`
- `docs/roadmap.md`
- `docs/safety-and-rollback.md`
- `docs/test-strategy.md`
- `docs/handoffs/milestone-8-handoff.md`

### Domain

All files under `src/CalibreLibraryCleaner.Domain/Recoveries/`:

- `RecoveryBackupChain.cs`
- `RecoveryExecution.cs`
- `RecoveryIssues.cs`
- `RecoveryOperations.cs`
- `RecoveryPlan.cs`
- `RecoveryPlanContentDigestPolicy.cs`
- `RecoveryPlanLifecyclePolicy.cs`
- `RecoveryPlanValues.cs`
- `RecoveryReconciliation.cs`
- `RecoveryRecordIdentity.cs`
- `RecoveryVerificationPolicy.cs`

### Application

- recovery ports under
  `src/CalibreLibraryCleaner.Application/Abstractions/IRecovery*.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ILibraryMutationLease.cs`
- all files under `src/CalibreLibraryCleaner.Application/Recoveries/`
- cleanup lease integration changes in
  `Executions/ExecuteApprovedCleanupPlanUseCase.cs` and
  `Executions/ExecutionContracts.cs`
- the superseded `ICleanupExecutionLease.cs` was removed

### Infrastructure

- all files under `src/CalibreLibraryCleaner.Infrastructure/Recovery/`
- `Calibre/CalibreRecoveryExecutionProfileProvider.cs`
- recovery mappings and profile changes in `Calibre/CalibreCommandGateway.cs`,
  `Calibre/CalibreExecutionOptions.cs`, and `Calibre/CalibreToolDiscovery.cs`
- `Execution/FileLibraryMutationLease.cs`
- lease and source-backup integration changes under `Execution/`
- registrations in `DependencyInjection/ServiceCollectionExtensions.cs`
- the superseded `Execution/FileCleanupExecutionLease.cs` was removed

### WPF

- `Services/IRecoveryWorkflowServices.cs`
- `Services/RecoveryWorkflowServices.cs`
- `ViewModels/RecoveryRows.cs`
- `ViewModels/RecoveryWorkspaceViewModel.cs`
- recovery composition and UI changes in `App.xaml.cs`, `MainWindow.xaml`, and
  `ViewModels/MainWindowViewModel.cs`

### Tests

- all tests under
  `tests/CalibreLibraryCleaner.Domain.Tests/Recoveries/`
- all tests under
  `tests/CalibreLibraryCleaner.Application.Tests/Recoveries/`
- all tests under
  `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recovery/`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Calibre/CalibreRecoveryCommandBoundaryTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/RecoveryWorkspaceViewModelTests.cs`
- cleanup/shared-lease, architecture, and controlled-helper updates in the
  existing execution and test-helper files

No package or project dependency was added.

## Recovery capability matrix

The closed Windows Calibre 9.11.0 mappings exist, but every production recovery
mutation capability is disabled by default until that exact capability passes
the optional disposable-library qualification.

| Capability | Closed mapping and controlled test | Production status |
| --- | --- | --- |
| Export current record | Existing typed `calibredb export`; strict inventory, OPF, cover, and format verification | Disabled pending exact qualification |
| Create empty record | `calibredb add --empty --title ... --authors ...` | Disabled pending exact qualification |
| Find created record | Command result plus unique fresh semantic scan delta | Disabled; ambiguity blocks |
| Add original format | `calibredb add_format` from a reverified external original backup | Disabled pending exact qualification |
| Replace format | Typed `add_format`, only after current backup and preservation dependencies | Disabled pending exact qualification |
| Restore metadata | Field-scoped `set_metadata --field` for title, authors, author sort, publisher, publication date, languages, identifiers, series, and series index | Disabled per field pending exact qualification |
| Restore cover | No qualified mapping | Unsupported and blocked |
| Remove cleanup-added format | Typed `calibredb remove_format` | Disabled pending exact qualification |
| Remove cleanup-created record | Existing typed non-permanent `calibredb remove` | Disabled pending exact qualification |
| Verify restored content | Complete fresh read-only semantic scan and exact format/cover hashes | Disabled as a profile capability pending exact qualification |

`remove --permanent`, `restore_database`, `backup_metadata`, `embed_metadata`,
shell commands, arbitrary arguments, GUI automation, and direct
database/filesystem repair are not capability candidates.

## Eligibility and reconciliation behavior

Eligibility separates blocking issues, acknowledgement-required warnings, and
informational differences. It blocks on missing/tampered/incompatible source
artifacts, mismatched plan/execution/library identities, unknown or
contradictory journal state, unverified original backup, incomplete current
scan, unsupported exact capability, stale plan, unsafe backup destination,
active shared lease, or unresolved conflicting recovery.

Reconciliation deterministically classifies records and formats as unchanged,
expected post-state, journal-confirmed completion, journal/current
disagreement, missing, execution-created, replaced, already restored,
independently recreated or modified, unexpected unique content, changed ID, or
ambiguous. It uses hashes and modeled semantic facts; filename, timestamp,
size, and numeric record ID are never sufficient to select a variant for
destruction.

Unexpected current content is preserved in place and included in the mandatory
current-state backup. When original semantic state cannot be restored on the
same record without overwriting current data, planning creates a separate
recovered record and restores all original formats there. If safe coexistence
or unique identity cannot be proven, the plan blocks for manual intervention.
Cover conflicts currently block because no cover restore mapping is qualified.

## Recovery-plan state model

The immutable body contains source plan/execution identities and canonical
hashes, original backup-chain identity, current library and state fingerprints,
reconciliation, dependency graph, preservation expectations, expected final
semantic state, and issues. Its canonical content digest does not change when
the artifact lifecycle changes.

Lifecycle revisions are create-only transitions:

```text
Draft -> Valid | Blocked
Valid -> Approved | Stale | Revoked
Approved -> Stale | Revoked | Completed
```

Approval binds the exact prior artifact revision, immutable-body digest, source
execution, library UUID and canonical root, current-state fingerprint, exact
capability profile, and the exact set of warning acknowledgements. A semantic
body change requires a new recovery plan ID.

## Original/current backup-chain design

The Milestone 7 backup and source artifacts are independently rehashed before
planning and again under the execution lease. They remain read-only.

Before mutation, recovery creates a distinct versioned bundle outside the
library. It stores the approved recovery plan, references and audit copies of
the source cleanup plan/journal/summary/original manifest, the current managed
inventory, Calibre exports, every current affected format, current covers,
unexpected content selected for preservation, and a manifest with sizes,
hashes, safe relative paths, semantic identities, and both backup-generation
identities. The manifest and every entry are recalculated and reverified.
Mutation cannot cross its durable marker unless both generations are available
and verified and a post-backup scan still matches.

## Execution phases, journal, lease, and failures

Execution gates are:

1. acquire the shared cleanup/recovery lease;
2. repeat source, tool, capability, approval, root, and current-state preflight;
3. create and independently verify the current-state backup;
4. rescan and reverify both backup generations before each command;
5. apply constructive operations at safe boundaries;
6. perform fresh intermediate semantic verification;
7. request separate confirmation bound to the exact destructive graph and
   verified current-backup digest;
8. apply destructive operations last without automatic retry;
9. perform a fresh final semantic scan and backup-chain verification.

Journal events are hash chained and create-only, including operation intent,
command result, semantic verification, cancellation request/stopping point,
phase markers, ID mappings, terminal classification, and immutable terminal
summary. Recovery history uses append-only execution entries. A successful
recovery adds a separate create-only resolution link; it never edits the
Milestone 7 evidence.

A constructive command or intermediate verification failure stops before
destruction and rescans, producing partial recovery when mutation occurred.
A destructive command failure stops, rescans, and produces
`ManualInterventionRequired`. Final semantic failure never reports success and
is `VerificationFailed` before destruction or
`ManualInterventionRequired` after destruction. No partial recovery is
automatically resumed.

## Record-ID behavior

Logical recovery identity is independent of Calibre numeric IDs. When Calibre
creates a record, the command result and a fresh before/after scan must identify
exactly one new semantic record. The journal then persists original ID,
cleanup-created/target ID when applicable, recovered ID, logical identity,
identifiers, and restored formats. A changed numeric ID is accepted only when
all expected semantic state and preservation expectations pass final
verification.

## Tests and commands run

Commands run on 2026-07-24:

```powershell
dotnet restore
dotnet restore --configfile .nuget-m8-verification.config
dotnet build --no-restore
dotnet test --no-build
dotnet test --no-build --filter "FullyQualifiedName~Recover"
dotnet test --no-build --filter "FullyQualifiedName~CalibreRecoveryCommandBoundaryTests|FullyQualifiedName~DependencyDirectionTests"
dotnet test --no-build --filter "Category=OptInRealCalibreRecovery"
dotnet format --no-restore
dotnet format --no-restore --verify-no-changes
dotnet format --verify-no-changes
git diff --check
```

Results:

- normal restore failed only with machine-level `NU1507`: nine configured feeds
  are not package-source-mapped under central package management;
- restore with a temporary single-source NuGet configuration succeeded; the
  temporary file was removed and repository package policy was not changed;
- build succeeded with zero warnings and zero errors;
- full automated tests: 386 passed, zero failed, two skipped;
- focused recovery tests: 54 passed, zero failed, one skipped opt-in
  qualification;
- focused command-boundary and architecture tests: 24 passed, zero failed;
- formatting with existing restored assets succeeded and verified clean;
- restore-performing `dotnet format --verify-no-changes` failed because it hit
  the same machine-level `NU1507` configuration;
- `git diff --check` reported no whitespace errors; and
- source-safety searches were reviewed in context and found no prohibited
  production behavior.

The skipped tests are the pre-existing caller-gated Milestone 7 real-Calibre
compatibility test and the new caller-gated Milestone 8 per-capability
qualification. The `OptInRealCalibreRecovery` filter found that test and
reported it skipped because the two explicit disposable-library variables were
not supplied; no real-Calibre recovery qualification was run.
Automated recovery tests use fakes, controlled helper executables, synthetic
manifests/journals, and temporary directories. They never discover or use the
default or production Calibre library.

## Unsupported scenarios and remaining risks

- Production recovery mutation is unavailable until individual capabilities
  pass exact Calibre 9.11.0 qualification and are deliberately enabled.
- Cover restoration is unsupported.
- Ambiguous record matching, reused numeric IDs without semantic identity,
  same-format coexistence that cannot use a separate record, unmodeled metadata,
  unreadable backup items, and unexpected data that cannot be preserved require
  manual intervention.
- External Calibre writers are not controlled by the application lease;
  repeated scans detect changes but cannot prevent them.
- Field-scoped metadata behavior, empty/null semantics, created-record output,
  hooks, localization, and collateral field behavior require real-Calibre
  qualification.
- Legacy Milestone 7 hashes detect ordinary tampering and substitution but
  cannot authenticate a coordinated hostile rewrite of every legacy artifact.
- Disk or journal failure after a Calibre mutation can still require manual
  intervention even though both backup generations remain intact.
- The WPF source selector accepts exactly one execution-bundle folder; it does
  not yet provide a richer browser over the Milestone 7 history index.

There were no architectural deviations from ADR 0008. The intentionally
uncompleted acceptance item is external exact-version qualification; no
capability was enabled by inference. No package was added.

## Next exact step

Run the caller-gated `OptInRealCalibreRecovery` disposable-library qualification
suite against the exact Calibre 9.11.0 executable and a caller-marked
operating-system temporary root. Review each capability result, then enable only
the individually passing capabilities in the immutable 9.11.0 recovery profile.
Do not enable recovery mutation as one aggregate switch and do not use a real
user library.
