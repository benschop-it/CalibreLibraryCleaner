# Persisted library snapshots

## Objective
Persist the latest complete successful `LibrarySnapshot` for each canonical Calibre library folder and let the user explicitly load a prior snapshot instead of rescanning.

## Scope
- Store one latest snapshot per canonical library root outside the Calibre library.
- List persisted library roots at WPF startup.
- Present those roots in a dropdown and provide an explicit Load command.
- Automatically replace the cached snapshot after a successful fresh scan.
- Restore the complete snapshot presentation, including books, findings, duplicate groups, EPUB/PDF assessments, and recommendations.
- Version and validate the persisted artifact and bound reads.

## Out of scope
- Partial or failed scan persistence.
- Multiple historical snapshots for one library root.
- Cache migration across unsupported artifact versions.
- Automatic loading without user action.
- Treating cached evidence as fresh execution or recovery preflight.
- Writing any artifact inside a Calibre library.

## Relevant requirements
- Product vision requires local-first operation and scale to tens of thousands of records.
- Architecture assigns analysis-cache persistence to Infrastructure, orchestration ports/use cases to Application, and interaction to WPF.
- Analysis must remain read-only with respect to the Calibre library.
- Long-running work must remain asynchronous and cancellable.

## Existing implementation inspected
- `LibrarySnapshot` is the immutable complete analysis result and validates assessment/recommendation associations.
- `ScanLibraryUseCase` publishes a snapshot only after all analysis phases complete.
- `MainWindowViewModel.ApplySnapshot` already builds and atomically applies all presentation collections.
- Existing JSON stores use strict `System.Text.Json`, bounded reads, temporary sibling files, write-through flush, and atomic replacement.
- `MainWindow` currently exposes Browse, Scan, and Cancel controls around one read-only selected path.

## Proposed design
Application declares `ILibrarySnapshotStore` with list, read, and write operations keyed by canonical library root. Application use cases expose listing, loading, and saving outcomes without filesystem or JSON types.

Infrastructure stores versioned JSON artifacts under `%LOCALAPPDATA%/CalibreLibraryCleaner/library-snapshots`. The filename is a SHA-256 digest of the canonical library root so paths are not used as filenames. The artifact records the canonical root and complete `LibrarySnapshot`; reads verify the requested key matches the embedded root. Windows path keys compare ordinal-ignore-case; other platforms compare ordinal. Writes use a temporary sibling, flush to disk, and atomically replace the previous file. Reads are size bounded and reject unknown schema versions, malformed JSON, duplicate JSON properties, reparse points, and inconsistent snapshot identities.

WPF initializes the persisted-root list before showing the window. A dropdown lists roots with a cached result. Selecting one updates the active library path; Load restores it through the same `ApplySnapshot` path used by a fresh scan. A successful scan remains visible even if cache publication fails, while the UI reports that persistence failed. Loading does not access or mutate the live library and clearly labels the result with its original scan time.

Cached snapshots are historical review evidence. Existing cleanup execution and recovery workflows still perform their mandatory fresh scans and revalidation before mutation.

## Files expected to change
- Add Application cache contracts and list/load/save use cases.
- Add Infrastructure storage options, strict serializer/converters, and file store.
- Register the store and use cases in Infrastructure/WPF composition.
- Extend `MainWindowViewModel` and `MainWindow.xaml` with persisted roots and Load behavior.
- Initialize persisted choices during WPF startup.
- Add Application, Infrastructure, WPF, and architecture tests.
- Update architecture and functional-requirement documentation.

## Safety considerations
- Never write in or beneath a selected Calibre library.
- Persist only complete successful snapshots.
- Do not deserialize unbounded or unknown data.
- Do not retain book content beyond the bounded evidence already present in `LibrarySnapshot`.
- Treat cache files as untrusted local input and fail closed without replacing current UI results.
- Never let cache loading bypass fresh cleanup/recovery execution validation.

## Implementation steps
1. Add Application contracts/outcomes and list/load/save use cases.
2. Implement and test strict snapshot JSON round-tripping.
3. Implement atomic latest-per-path storage and listing.
4. Register services in composition.
5. Add dropdown, Load command, startup initialization, and automatic save.
6. Add focused ViewModel and architecture coverage.
7. Run formatting, build, relevant tests, and full tests.

## Tests
- Complete snapshot round-trip preserves every collection and identity.
- Unknown versions, malformed JSON, oversized files, key mismatch, and unsafe files fail closed.
- Writing twice for one canonical path leaves only the latest snapshot.
- Distinct paths list independently and deterministically.
- Application use cases map store outcomes and cancellation.
- WPF startup lists persisted roots, selection sets the path, Load applies prior results, and successful scans save.
- Persistence failure does not discard a successful live scan.

## Verification commands
```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

## Risks
- Constructor-based JSON deserialization must handle wrapper assessments without serializing convenience properties as independent state.
- Large snapshots require explicit file and collection bounds.
- Library paths may differ only by case or trailing separators on Windows and must map to one key.
- Loaded evidence may be stale; UI wording and existing live execution revalidation must remain explicit.

## Unresolved questions
None for the requested slice. Retention limits, cache deletion UI, migrations, and multi-version history are deferred.

## Progress
- [x] Existing persistence, snapshot, and WPF patterns inspected.
- [x] Path-keyed latest-only design selected.
- [x] Application contracts and use cases implemented.
- [x] Infrastructure serializer and store implemented.
- [x] WPF selector/load/save flow implemented.
- [x] Tests and verification complete.

## Final outcome
The application now atomically persists one complete versioned snapshot per canonical library folder under local application data. Startup lists persisted paths in the library dropdown; Load restores the selected historical analysis, and every complete fresh scan replaces that path's cached result. Historical loads are clearly marked potentially stale and cannot populate cleanup, execution, or recovery contexts until a fresh scan succeeds. Full build, 524 tests (522 passed and two opt-in real-Calibre tests skipped), architecture tests, real-window startup, and formatting verification succeeded.
