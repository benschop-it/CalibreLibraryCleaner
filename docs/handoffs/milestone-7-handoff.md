# Milestone 7 Safe Calibre Execution Handoff

## Purpose and baseline

This handoff is the starting point for a fresh Codex conversation continuing
Milestone 7. Do not rely on prior chat history.

- Repository: `CalibreLibraryCleaner`
- Branch at handoff: `master`
- Milestone 6 baseline: `fe83000`
- Milestone 7 implementation and safety hardening: `607a829`
- Handoff baseline before this document: `12871b7`
- Runtime: .NET 10, Windows WPF

Before changing code, read:

1. `AGENTS.md`, `PLANS.md`, and every nested `AGENTS.md`.
2. `docs/plans/milestone-7-safe-calibre-execution.md`.
3. `docs/adr/0001-read-only-calibre-access.md`,
   `docs/adr/0002-clean-architecture.md`,
   `docs/adr/0003-calibre-cli-for-writes.md`, and
   `docs/adr/0007-safe-calibre-command-execution.md`.
4. `docs/architecture.md`, `docs/domain-model.md`,
   `docs/safety-and-rollback.md`, `docs/test-strategy.md`, and
   `docs/roadmap.md`.
5. `docs/workflows/review-feature.md`.

The complete Milestone 7 diff is:

```powershell
git diff fe83000..607a829
```

## Completed work

Milestone 7 implements a single-plan, serial, fail-closed execution slice:

- a separate immutable execution aggregate and deterministic operation graph;
- local confirmation bound to the immutable plan, canonical library root,
  library UUID, operation-graph digest, tool identity, and backup destination;
- exact Calibre 9.11.0 discovery and a closed capability profile;
- a cross-process application lease keyed by canonical root and library UUID;
- complete fresh read-only preflight and a repeated complete gate immediately
  before every mutating command;
- complete external recovery bundles with raw format copies, Calibre exports,
  metadata OPF, plan, confirmation, identities, managed-state evidence,
  journal, terminal summary, and independently verified hashes;
- direct no-shell `calibredb` execution using `ArgumentList`;
- constructive `add_format` operations before non-permanent source-record
  `remove` operations;
- full fresh semantic verification after every command and at completion;
- hash-chained write-ahead journaling and immutable terminal summaries;
- an application-local recovery guard written before the first mutation marker;
- durable `RecoveryRequired` behavior for every uncertain post-marker outcome;
- safe-stop cancellation that never kills a mutating Calibre process;
- WPF review, acknowledgements, confirmation, progress, safe stop, history, and
  recovery-required presentation; and
- controlled-executable, Domain, Application, Infrastructure, architecture,
  WPF, and opt-in real-Calibre test coverage.

The safety-critical review was incorporated into the implementation. It fixed:

- library-root and operation-graph substitution after confirmation;
- freshness races between the post-backup gate and individual commands;
- recovery-state bypass through a different backup parent;
- completed journal tails without a matching immutable terminal summary;
- executable and selected-format backup replacement after hashing;
- unsafe reparse-point and external-storage traversal;
- arbitrary child-process environment inheritance;
- missing local-confirmation coverage in the backup manifest;
- fail-open capability lookup for unresolved formats;
- application close during an active non-terminable mutation; and
- cover-content verification gaps.

## Current implementation state

The code slice is implemented and all non-opt-in automated tests pass. It is not
yet qualified for real-library use.

The only candidate production profile is Windows Calibre 9.11.0:

- supported mutations: `add_format` addition or explicitly reviewed
  replacement, and non-permanent `remove` of a redundant source record;
- verified no-ops: target metadata and already-correct target formats;
- unsupported: present covers, cross-record metadata or cover transfer,
  standalone target-format removal, extra-data mutation, arbitrary commands,
  permanent removal, bulk execution, retry, resume, repair, and rollback.

`CalibreExecutionOptions.IsValidatedCompatibilityProfileEnabled` defaults to
`false`. WPF therefore fails closed with
`EXECUTION.CALIBRE_PROFILE_NOT_VALIDATED`. Do not enable it until the opt-in
Calibre 9.11.0 disposable-library test passes and its results are reviewed.

All analysis and verification database access remains read-only. Production
code contains no direct write to `metadata.db` and no manual mutation of
Calibre-managed files or directories. The only library mutations are the typed
gateway calls to `calibredb add_format` and non-permanent `calibredb remove`.

## Files changed

Primary Milestone 7 files are listed below. Use
`git diff --name-status fe83000..607a829` for the authoritative complete list.

### Documentation and solution

- `CalibreLibraryCleaner.sln`
- `Directory.Packages.props`
- `docs/adr/0007-safe-calibre-command-execution.md`
- `docs/architecture.md`
- `docs/domain-model.md`
- `docs/plans/milestone-7-safe-calibre-execution.md`
- `docs/roadmap.md`
- `docs/safety-and-rollback.md`
- `docs/test-strategy.md`

### Domain

- `src/CalibreLibraryCleaner.Domain/Executions/BackupManifest.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/CleanupExecution.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/CleanupExecutionOperation.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/CleanupExecutionValues.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/CleanupExecutionVerificationPolicy.cs`

### Application

- execution contracts under
  `src/CalibreLibraryCleaner.Application/Abstractions/`
- `src/CalibreLibraryCleaner.Application/Executions/ExecuteApprovedCleanupPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Executions/ExecutionContracts.cs`
- `src/CalibreLibraryCleaner.Application/Executions/ExecutionPreflightPolicy.cs`
- `src/CalibreLibraryCleaner.Application/Executions/FullExecutionLibraryScanner.cs`
- `src/CalibreLibraryCleaner.Application/Executions/PrepareCleanupExecutionUseCase.cs`

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreCommandGateway.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreExecutionOptions.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreToolDiscovery.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/DirectCalibreProcessRunner.cs`
- all files under `src/CalibreLibraryCleaner.Infrastructure/Execution/`
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

### WPF

- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml.cs`
- execution services under `src/CalibreLibraryCleaner.Wpf/Services/`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/CleanupExecutionRows.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/CleanupExecutionWorkspaceViewModel.cs`
- composition changes in
  `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`

### Tests

- `tests/CalibreLibraryCleaner.Domain.Tests/Executions/`
- `tests/CalibreLibraryCleaner.Application.Tests/Executions/`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Execution/`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/CleanupExecutionWorkspaceViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- controlled helper project under `tests/CalibreLibraryCleaner.TestCalibre/`

Repository line-ending policy was subsequently added in `.gitattributes` and
`.editorconfig`; Rider's local `workspace.xml` is intentionally no longer
tracked.

## Commands and tests run

Initial Milestone 7 verification on 2026-07-19:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
```

That run passed 334 tests, failed zero, and skipped the one opt-in real-Calibre
test.

Safety-hardening verification on 2026-07-24:

```powershell
dotnet restore --configfile .nuget-verification.config
dotnet build --no-restore
dotnet test --no-build
dotnet format style --verify-no-changes --no-restore
dotnet format analyzers --verify-no-changes --no-restore
git diff --check
```

The temporary verification config contained only:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

Results:

- restore succeeded;
- build succeeded with zero warnings and errors;
- tests passed 344, failed zero, skipped one;
- style and analyzer verification succeeded;
- the skipped test was
  `RealCalibreCompatibilityTests.ExactCalibreProfileMutatesOnlyCallerMarkedDisposableLibrary`.

The controlled tests cover exact argument tokens, nonzero exits without retry,
bounded/redacted output, no arbitrary inherited environment, no killing of
mutating processes, executable and backup substitution resistance, complete
backup coverage, hash rechecks, lease exclusion, reparse rejection, journal
reconciliation, recovery guards, per-command freshness, confirmation binding,
partial-failure classification, WPF presentation, and architecture boundaries.

## Unresolved failures

### Real Calibre qualification

The opt-in exact-version test has not run because `CALIBRE_TEST_EXE` and
`CALIBRE_TEST_ROOT` were not supplied. The Calibre available during
implementation was 7.14, not the accepted 9.11.0 profile. Controlled helper
executables prove process mechanics but cannot prove real Calibre semantics.
This is the remaining Milestone 7 release blocker.

No manual success/failure/crash exercise has been performed with real Calibre
9.11.0. Automatic rollback must not be added as a workaround; it belongs to
Milestone 8.

### Machine NuGet configuration

The standard unisolated build currently fails with `NU1507` because the machine
defines nine NuGet sources while central package management and
warnings-as-errors are enabled. A temporary config containing only nuget.org
restores and builds successfully. Do not add machine-specific corporate source
mapping to the repository without an explicit repository policy decision.

### Current checkout line endings

The Git index is normalized and `.gitattributes` declares CRLF for repository
text with LF exceptions for `.gitattributes`, `.editorconfig`, and shell
scripts. Nevertheless, this working directory currently contains tracked files
physically checked out with LF or mixed endings, so:

```powershell
dotnet format whitespace --verify-no-changes --no-restore
```

fails with `ENDOFLINE`. A fresh local clone was verified to apply the committed
attributes without mixed or mismatched endings. Treat this as a checkout/tooling
problem and keep any mechanical correction separate from Milestone 7 code.

## Safety decisions that must not be weakened

- Never write directly to `metadata.db`.
- Never manually mutate Calibre-managed paths.
- Invoke only the canonical, hash-verified executable; never use a shell.
- Keep the executable and selected-format backup handle locked through process
  completion.
- Bind approval and local confirmation to canonical immutable identities.
- Hold the external application lease through durable terminal persistence.
- Require complete independently rehashed backups before `MutationStarting`.
- Persist the application-local recovery guard before the journal mutation
  marker.
- Repeat a complete fresh scan and every identity/backup/lease check before
  each command.
- Add or replace retained content constructively and verify it before removing
  any source record.
- Treat process exit zero only as transport success; require semantic scans.
- Never kill an active mutating Calibre process.
- Never retry or resume a partial mutation.
- Treat every uncertain post-marker outcome as durable `RecoveryRequired`.
- Require the final hash-chained journal event and immutable terminal summary
  to agree.
- Reject reparse points and any backup/config/history/lease/journal location
  resolving in or around the library.
- Keep ebook content and sensitive paths out of ordinary logs.
- Block cover-bearing plans until byte-exact cover state is modeled.
- Do not implement rollback, repair, or other Milestone 8 behavior.

## Next exact implementation step

Qualify the exact Calibre 9.11.0 profile using only the existing opt-in test and
a caller-marked disposable parent under the operating-system temporary
directory.

1. Obtain and independently identify the intended Windows Calibre 9.11.0
   `calibredb.exe`. Do not use the installed 7.14 executable.
2. Create a new empty parent beneath `Path.GetTempPath()`.
3. Create an empty marker named
   `.calibre-library-cleaner-disposable-test-root` in that parent.
4. Confirm the parent itself does not contain `metadata.db`.
5. Set the two opt-in variables and run only the compatibility test:

```powershell
$env:CALIBRE_TEST_EXE = 'C:\path\to\Calibre-9.11.0\calibredb.exe'
$env:CALIBRE_TEST_ROOT = Join-Path ([IO.Path]::GetTempPath()) 'CalibreLibraryCleaner-Calibre911'
New-Item -ItemType Directory -Force -Path $env:CALIBRE_TEST_ROOT
New-Item -ItemType File -Force -Path (
    Join-Path $env:CALIBRE_TEST_ROOT '.calibre-library-cleaner-disposable-test-root')

dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj `
    --no-build `
    --filter 'Category=OptInRealCalibre'
```

The test creates a child library, exports a record, adds a PDF, replaces that
PDF, removes the second record non-permanently, verifies each state through the
read-only scanner, and deletes only its generated child directory.

If and only if it passes:

1. inspect the disposable library and test output for unrelated changes;
2. record the executable version/hash and qualification result in the Milestone
   7 plan and ADR 0007;
3. make a separate reviewed change to enable the validated production profile;
4. rerun restore, build, all automated tests, full formatting, `git diff
   --check`, and the complete Milestone 6-to-current safety diff.

If it fails, leave the profile disabled, preserve the failure evidence, and fix
only the smallest documented compatibility discrepancy. Do not broaden the
version range, infer undocumented behavior, bypass a failed gate, or test
against a real user library.
