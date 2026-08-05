# Unified Development Snapshots and Simplified Cleanup

## Objective

Keep persisted analysis loading throughout development so the developer can test ordinary code changes without repeating a roughly twenty-minute library scan. Consolidate duplicate snapshot/state persistence into one manifest-driven state family and reduce mutation functionality to the proven one-click exact-duplicate worker path.

## Scope

- Preserve startup listing and explicit loading of persisted analysis during development.
- Migrate existing `.library-snapshot.json` artifacts into state generations without rescanning.
- List persisted libraries from small manifests and retain only the active generation.
- Keep typed projected-state deltas and checkpointing for fast restart and accurate UI state.
- Keep one-click exact duplicate cleanup through one persistent `calibre-debug` worker.
- Require per-run confirmation that the developer or user has a complete external backup.
- Log failures, stop on failed or ambiguous mutation, and require explicit Rescan.
- Remove general cleanup-plan execution, app-created backups, CLI mutation fallback, and automated recovery.

## Out of scope

- Removing persisted analysis loading during this implementation.
- Removing the in-memory `LibrarySnapshot` domain model.
- Direct SQLite or Calibre-managed filesystem mutation.
- Retrying or continuing after an ambiguous mutation.
- Automated restore or production migration guarantees for development caches.
- Changes to analysis, assessment, recommendation, or duplicate-detection policy.

## Relevant requirements

- A scan may take approximately twenty minutes on the development library; persisted loading is required for an efficient edit/run/test loop.
- Exact cleanup remains explicit and uses supported Calibre tooling.
- The application never writes directly to `metadata.db` or directly mutates Calibre-managed files.
- Mutation failures are logged. Any possibly partial or ambiguous mutation stops and blocks further mutation until explicit Rescan.
- The application does not create or verify backups; each run requires confirmation that a complete external backup exists.

## Existing implementation inspected

- `VersionedJsonLibrarySnapshotStore` stores one complete `.library-snapshot.json` per library and now lists it through a metadata-only prefix read.
- `VersionedJsonLibraryStateStore` stores an atomic manifest, immutable baseline/checkpoint, and hash-chained delta journal, but cannot list manifests and does not prune prior generations.
- A successful scan writes the same complete `LibrarySnapshot` to both stores.
- Startup lists the snapshot store; explicit Load prefers state replay and falls back to the snapshot cache.
- Exact cleanup prefers one persistent worker but retains a slower `calibredb` fallback and per-chunk mutation intents.
- General cleanup execution, app-created backup, audit history, and verified recovery remain registered and visible in technical tabs.

## Proposed design

### Unified development persistence

`ILibraryStateStore` becomes the application persistence contract. Its lightweight listing operation reads only `*.library-state.json` manifests. New scans write only a state baseline, empty journal, and manifest. Existing snapshot-only entries remain discoverable through an internal legacy adapter; explicit Load strictly reads the selected snapshot, publishes it as a revision-zero state generation, and removes the legacy file after publication.

Manifest publication is the commit point. After publication, the store deletes all same-library baseline, checkpoint, and journal artifacts not referenced by the manifest. Cleanup failure is a warning and is retried after a later publication. Checkpointing remains after successful cleanup and at the configured threshold. Shutdown performs no compaction or large-file write.

### Simplified exact cleanup

The one-click workflow remains the only mutation workflow. A request carries explicit external-backup acknowledgement. It acquires the existing lease, opens one trusted persistent worker, and sends deterministic chunks of at most 100 operations. Worker startup failure stops before mutation; no CLI fallback runs.

One durable run marker replaces detailed recovery-oriented per-chunk intents. A chunk is projected only after the complete worker chunk succeeds. Any failure or ambiguity after mutation starts emits structured logs, marks state Rescan-required, and stops. No retry, inferred successful prefix, or automated recovery is attempted.

### Removed surfaces

General cleanup-plan authoring/execution, application-created backups, execution journals/history, direct command fallback, and automated recovery domain/application/infrastructure/WPF surfaces are removed after the worker-only path and unified persistence are validated.

## Files expected to change

- `src/CalibreLibraryCleaner.Application/Abstractions/ILibraryStateStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ILibrarySnapshotStore.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryStateSession.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/PersistedLibrarySnapshotsUseCase.cs`
- `src/CalibreLibraryCleaner.Infrastructure/LibrarySnapshots/VersionedJsonLibraryStateStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/LibrarySnapshots/VersionedJsonLibrarySnapshotStore.cs`
- `src/CalibreLibraryCleaner.Application/Executions/ExecuteBulkExactDuplicateCleanupUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Executions/BulkExactDuplicateCleanupContracts.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/PersistentCalibreMutationWorkerFactory.cs`
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/ExactBinaryCleanupPlanWorkspaceViewModel.cs`
- Relevant domain, application, infrastructure, WPF, and architecture tests.

## Safety considerations

- Never delete the baseline/checkpoint or journal referenced by the active manifest.
- Publish a new manifest atomically before pruning superseded artifacts or deleting a migrated snapshot.
- Legacy snapshots are trusted developer cache data by explicit decision; this is not a production freshness guarantee.
- Keep the library mutation lease, deterministic planning, typed worker protocol, operation ordering, and projected state.
- Log operation identifiers and technical outcomes, but never book content or format payloads.
- Do not continue or retry after an ambiguous mutation.

## Implementation steps

1. Add accepted ADRs and align authoritative product/safety documentation.
2. Add manifest-only state listing and current-generation pruning.
3. Route new scan persistence through the state store only.
4. Add strict legacy snapshot migration on explicit Load.
5. Update WPF persisted library listing/loading and validate against the large development cache.
6. Add backup acknowledgement and structured mutation logging.
7. Remove CLI fallback and simplify worker chunk projection/run markers.
8. Remove general plan execution, app backup/history, and automated recovery surfaces.
9. Simplify composition, UI, architecture rules, and tests.
10. Complete repository and disposable-library verification.

## Tests

- Manifest-only listing does not read large baseline/checkpoint files.
- Legacy snapshots list and migrate without scanning; interrupted migration remains recoverable.
- New scans and migrated entries leave one active large state file, one journal, and one manifest.
- Multi-generation and checkpoint publication prune only unreferenced artifacts.
- Exact cleanup requires external-backup acknowledgement.
- Worker startup failure never invokes CLI fallback.
- Multi-chunk success projects complete chunks and checkpoints state.
- Mutation failure logs, stops later chunks, persists Rescan-required state, and explicit Scan clears it.
- WPF startup/load remains usable without a scan.
- Architecture tests prove removed mutation/recovery boundaries do not return.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
```

Also run focused persistence, exact-cleanup, architecture, and WPF startup tests, plus a disposable-library worker acceptance test. If a running process locks build outputs, report the process and wait rather than redirecting artifacts.

## Risks

- A trusted development snapshot can be stale relative to external Calibre changes.
- Deleting a legacy snapshot too early could remove the only usable development cache.
- Pruning patterns must be strictly scoped to one hashed library key.
- Removing broad execution/recovery surfaces has a large compile-time dependency graph.
- Structured logging needs a configured durable development provider if logs must survive application exit.

## Unresolved questions

- The durable log provider and retention policy will be selected while implementing the logging slice; the use case will depend only on `ILogger`.
- Final removal of persisted development loading requires a separate explicit decision after development acceptance.

## Progress

- [x] Existing persistence, startup, cleanup, backup, fallback, recovery, UI, tests, and ADRs inspected.
- [x] Product decisions recorded: keep development snapshots now; worker-only cleanup; external backup; log and stop on failure; remove automated recovery.
- [x] Execution plan and superseding ADRs added.
- [x] Authoritative requirements and architecture documents aligned.
- [x] Unified persistence implemented and validated.
- [x] Worker-only cleanup implemented and validated.
- [x] Obsolete systems removed and validated.
- [ ] Destructive disposable-library acceptance requires an explicitly supplied disposable Calibre library.

## Final outcome

Implementation is complete. `dotnet restore`, `dotnet build --no-restore`, `dotnet test --no-build`, and `dotnet format --verify-no-changes` succeeded on 2026-08-05. The automated suite completed with 452 passed, zero failed, and zero skipped. Architecture tests passed, `git diff --check` reported no errors, and the direct/transitive package audit reported no known vulnerabilities.

Focused verification proved manifest-only state listing without baseline access, strict metadata-only legacy listing, legacy migration after state publication, redundant legacy-cache deletion after authoritative replay, generation pruning, bounded run markers across multiple worker chunks, complete-chunk projection, external-backup acknowledgement, structured mutation-failure logging, worker-only startup failure, uncertainty persistence, numeric EPUB score sorting, and WPF XAML activation.

An actual WPF process reached a responsive main window against the existing development cache. Its automated `CloseMainWindow` request did not terminate the host within ten seconds, so the developer closed that process manually before final validation. No destructive test was run against the developer's real library. Disposable-library worker acceptance remains a manual follow-up requiring an explicitly supplied disposable Calibre library.