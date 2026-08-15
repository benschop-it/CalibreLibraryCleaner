# Milestone 2 Hashing and Exact Binary Duplicates

## Objective

Deliver the next read-only vertical slice of Calibre Library Cleaner. After the user selects and scans a supported Calibre library, the application streams every safely resolved existing ebook format through SHA-256, uses bounded concurrency, reports progress, supports responsive cancellation, converts expected per-file failures into structured findings, identifies files with the same byte length and SHA-256 digest, and displays deterministic exact binary duplicate groups with the Calibre records and formats involved.

This milestone identifies duplicate files, not duplicate book records. A group proves only that the listed managed format files were byte-identical when they were hashed. It must not imply that the containing records have matching metadata, represent the same edition, or should be merged or deleted.

The milestone is complete only when synthetic SQLite/filesystem fixtures exercise the end-to-end WPF flow and safety tests prove that successful, failed, and canceled analysis does not modify `metadata.db` or any file in the selected library.

## Scope

- Preserve the Milestone 1 folder validation, schema-27 read-only catalog load, safe managed-path resolution, findings, progress, cancellation, and WPF book/format display.
- Calculate SHA-256 for every safely resolved format file that can be opened and remains stable for the duration of hashing.
- Read files asynchronously in fixed-size chunks; never load a complete ebook into memory.
- Bound simultaneous file reads to an explicit, testable maximum.
- Report monotonic, throttled hashing progress without flooding the WPF dispatcher.
- Propagate cancellation through scheduling, file opening, every asynchronous read, result mapping, and duplicate grouping.
- Classify a format that is missing, inaccessible, unsafe to open, or observed changing during hashing as a structured finding; continue analyzing unrelated files.
- Retain the stable file size and canonical SHA-256 digest for each successfully hashed format.
- Detect exact binary duplicate files using both file size and SHA-256.
- Produce immutable, deterministic exact binary duplicate groups and stable group identities.
- Display groups and their member record IDs, titles/authors, formats, sizes, hashes, and relative paths in a minimal WPF view.
- Label and explain the view as file-level evidence, distinct from record-level duplicate detection.
- Extend synthetic fixtures and unit, integration, architecture, UI, performance-oriented, and safety tests only as needed for this slice.

## Out of scope

- Title/author normalization or matching, identifier grouping, fuzzy matching, edition inference, or any other duplicate-book-record algorithm.
- EPUB ZIP/package/content inspection, PDF inspection, text extraction, normalized content fingerprints, or malformed-ebook analysis.
- Quality or metadata scoring, confidence beyond the exact-binary reason, recommendations, auto-selection, or review decisions.
- Merging, deleting, replacing, renaming, moving, copying, repairing, or otherwise modifying formats or records.
- Cleanup plans, approval, backups, rollback, audit history, Calibre CLI discovery/invocation, or any mutation path.
- Persistent hash caches, incremental scans, settings UI, configurable user preferences, or database schema changes.
- Reading arbitrary files not represented by a safely resolved Calibre `data` row.
- Treating equal size alone as duplicate evidence.
- Depending on a user's installed Calibre, real library, or network access in automated tests.

## Relevant requirements

- `AGENTS.md`: analysis is read-only; long-running work is asynchronous, streaming, bounded, cancellable, and progress-reporting; paths remain canonicalized; expected problems become findings; tests use synthetic fixtures.
- `PLANS.md`: this cross-project, performance-sensitive, safety-sensitive change uses the required execution-plan structure and must remain current during implementation.
- `docs/product-vision.md`: local-first analysis, explainability, and safety take priority over aggressive automation.
- `docs/functional-requirements.md`: calculate streaming SHA-256 with cancellation, progress, and bounded concurrency; exact binary comparison is the first duplicate signal.
- `docs/architecture.md`: hashing belongs in Infrastructure behind Application-owned ports; duplicate invariants belong in Domain; WPF remains presentation/composition.
- `docs/domain-model.md`: snapshots, formats, findings, and duplicate groups are immutable and integration-free.
- `docs/duplicate-detection.md`: exact binary means matching SHA-256; this milestone must expose that reason without expanding into metadata or text matching.
- `docs/test-strategy.md`: cover hashes, grouping, cancellation, missing files, focused UI behavior, architecture boundaries, and full library non-mutation with synthetic fixtures.
- `docs/roadmap.md`: Milestone 2 is streaming hashing, bounded concurrency, exact duplicate grouping, and display only.
- ADR 0001: `metadata.db` remains strictly read-only and the SQL surface remains mutation-free.
- ADR 0002: preserve `Domain <- Application <- Infrastructure` and `Application <- Wpf`; WPF references Infrastructure only in `App.xaml.cs`.
- ADR 0003: no Calibre CLI or mutation implementation is introduced.
- Nested project/test `AGENTS.md` files: Domain stays free of filesystem/cryptography implementations, Application owns ports/use cases, Infrastructure owns file I/O, WPF uses MVVM, and tests are deterministic and offline.

## Existing implementation inspected

- The solution contains Domain, Application, Infrastructure, and WPF production projects plus Domain, Application, Infrastructure, Architecture, and WPF test projects on .NET 10.
- Shared build settings enable nullable references, latest C#, recommended analyzers, warnings as errors, deterministic builds, and formatting enforcement.
- Milestone 1 currently accepts Calibre 9.11/schema 27, opens `metadata.db` with `SqliteOpenMode.ReadOnly`, disables pooling, confirms `PRAGMA query_only = ON`, and issues only fixed `SELECT`/read-only `PRAGMA` statements.
- `ScanLibraryUseCase` validates, reads catalog records, resolves expected paths, calls `ILibraryPathResolver.FileExistsAsync`, constructs `BookFormat` values with `Present`, `Missing`, or `InvalidPath`, and returns a `LibrarySnapshot`.
- `FileExistsAsync` returns only `bool`. Because `File.Exists` also returns false for some access failures, this abstraction cannot meet Milestone 2's required missing-versus-inaccessible classification and should be replaced at the hashing boundary.
- `BookFormat` has no byte length or digest. `LibrarySnapshot` has books/findings only. No duplicate-domain types or detector exist.
- Infrastructure has no hashing implementation or cryptography package. .NET 10's BCL already provides asynchronous `FileStream`, `FileOptions.Asynchronous | FileOptions.SequentialScan`, `ArrayPool<byte>`, and incremental SHA-256; no third-party hashing dependency is needed.
- `MainWindowViewModel` owns a single scan cancellation token, commits results only on success, reports current phase percentage, and exposes book/selected-format rows. `MainWindow.xaml` has only the Milestone 1 book/format master-detail view.
- Existing synthetic fixtures create a minimal schema-27 SQLite catalog and tiny format files. `LibraryStateCapture` computes test-only SHA-256 with `File.ReadAllBytes`; it should also be changed to stream so large hashing fixtures do not make the safety harness itself unbounded.
- Existing safety tests compare recursive entry names, attributes, sizes, timestamps, and hashes before/after present, missing, and canceled scans and assert that no SQLite sidecars are created.
- Existing architecture tests enforce project-reference direction, prohibit integration dependencies in core projects, and prohibit Infrastructure/filesystem use in WPF ViewModels.
- At plan creation, the working tree already contains the completed Milestone 1 schema-27/startup fixes plus unrelated `.gitignore`, `.idea/workspace.xml`, and IDE state. Milestone 2 implementation must preserve and not overwrite unrelated user changes.

## Proposed design

### End-to-end flow

1. Keep `MainWindowViewModel.ScanCommand` as the single user action and keep revalidation/read-only catalog loading unchanged.
2. `ScanLibraryUseCase` resolves every declared format path. Invalid paths immediately become `MANAGED_PATH_INVALID`; every safely resolved path becomes a `FormatHashRequest` without a preliminary `File.Exists` decision.
3. The use case calls the Application-owned `IFormatFileHasher` batch port with all resolved requests, an explicit concurrency policy, progress, and the scan cancellation token.
4. Infrastructure preflights each path without mutation, classifies files that cannot be hashed, streams eligible files through SHA-256 with bounded workers, detects observable changes, and returns one provider-neutral result for every request in request sequence order.
5. The use case maps failures to findings and successful hashes to immutable `BookFormat` fingerprints, then constructs the complete `LibrarySnapshot`.
6. The Domain exact-binary detector groups successfully hashed format references by `(size, SHA-256)`, rejects singleton groups, validates member uniqueness, and returns deterministic immutable groups.
7. Only after hashing and grouping complete does the ViewModel atomically replace its book, format, finding, and duplicate-group rows. Cancellation or a fatal scan error leaves previous results explicitly identified as previous results.

Expected problems affecting one file never abort unrelated analysis. Cancellation throws `OperationCanceledException`. An unexpected service-wide hashing failure that prevents a trustworthy complete result becomes an actionable top-level `LibraryError`; raw paths, provider messages, and book content are not shown to the user.

### Proposed Domain types

All types remain immutable, defensively copy collections, and expose no `System.IO`, stream, cryptography implementation, WPF, logging, or SQLite type.

- `Sha256Digest`: a value object containing exactly 64 canonical lowercase hexadecimal characters (32 bytes). Parsing/validation is deterministic; SHA calculation remains outside Domain.
- `FormatFileFingerprint(long SizeInBytes, Sha256Digest Sha256)`: requires a non-negative size and represents the stable facts used for exact equality.
- `BookFormat` gains `FormatFileFingerprint? Fingerprint`. A final `Present` format must have a fingerprint; `Missing`, `InvalidPath`, `Inaccessible`, and `ChangedDuringHashing` formats must not. Construction should make invalid status/fingerprint combinations impossible.
- `FormatFileStatus` retains `Present`, `Missing`, and `InvalidPath` and adds `Inaccessible` and `ChangedDuringHashing`. `Present` in a completed Milestone 2 snapshot means successfully hashed and stable for the observed read.
- `ExactBinaryDuplicateGroupId`: a canonical stable value derived from the fingerprint, for example `sha256:<digest>:<size>`. It does not depend on member enumeration order and remains stable if another matching member appears in a later scan.
- `ExactBinaryDuplicateMember(CalibreBookId BookId, string Format, string ExpectedRelativePath)`: identifies one Calibre-managed format file without duplicating display metadata.
- `ExactBinaryDuplicateGroup(ExactBinaryDuplicateGroupId Id, FormatFileFingerprint Fingerprint, IReadOnlyList<ExactBinaryDuplicateMember> Members)`: requires at least two distinct member references, rejects duplicate `(BookId, Format, RelativePath)` entries, and exposes `DistinctBookCount`/`SpansMultipleBookRecords` so the UI can state exactly what the group means.
- `ExactBinaryDuplicateDetector.Detect(IEnumerable<CalibreBook>)`: a deterministic Domain service that considers only formats with fingerprints, groups by size then digest, and returns immutable ordered groups.
- `LibrarySnapshot` gains `IReadOnlyList<ExactBinaryDuplicateGroup> ExactBinaryDuplicateGroups` and defensively copies it. A compatibility overload/default may minimize call-site churn, but all production scans explicitly supply the groups.

`docs/domain-model.md` currently states generically that duplicate groups contain at least two distinct records. Milestone 2 must refine that statement: a record-duplicate group requires distinct records, while an exact binary file group requires distinct managed file references and reports how many records they span. This is necessary to clearly distinguish the concepts requested by this milestone.

### Application interfaces, contracts, and use case

- Add `IFormatFileHasher.HashAsync(IReadOnlyList<FormatHashRequest> requests, int maxDegreeOfParallelism, IProgress<FormatHashProgress>? progress, CancellationToken cancellationToken)`.
- `FormatHashRequest` contains a deterministic sequence number, `CalibreBookId`, canonical format token, and existing `ResolvedFormatPath`. Absolute paths remain Application/Infrastructure values and never enter Domain snapshots or logs.
- `FormatHashResult` returns the matching request identity, `FormatHashResultStatus`, an optional `FormatFileFingerprint`, and a safe reason code. Results must contain exactly one entry per request and be ordered by request sequence regardless of worker completion order.
- `FormatHashResultStatus` has `Success`, `Missing`, `Inaccessible`, and `ChangedDuringHashing`. Unexpected programmer/crypto failures are not mislabeled as file findings.
- `FormatHashProgress` contains completed/total bytes, completed/total files, and a safe message. Infrastructure reports it; `ScanLibraryUseCase` maps it to `LibraryScanProgress`.
- `LibraryAnalysisOptions` contains a validated `MaxHashConcurrency`; the proposed default is four concurrent file reads. WPF composition and Infrastructure integration tests register it explicitly.
- Remove `ILibraryPathResolver.FileExistsAsync`. Opening and inspecting the exact resolved file in the hasher becomes the authoritative existence/access decision, preventing `File.Exists` from collapsing access failures into missing-file findings.
- Extend `LibraryScanPhase` with `HashingFormats` and `GroupingExactDuplicates` before `Completed`.
- Extend `LibraryErrorCode` with a safe service-wide hashing/analysis failure code only if the use case needs to convert an unexpected batch failure. Per-file expected failures remain findings.
- Continue using `ScanLibraryUseCase` as the focused user-visible operation. It orchestrates catalog loading, path resolution, hashing, finding mapping, and exact grouping; it does not perform filesystem or hash calculations itself.

New/updated finding codes:

- `FORMAT_FILE_MISSING`: the resolved file was absent before it could be opened. It is excluded from grouping.
- `FORMAT_FILE_INACCESSIBLE`: the path is not a readable regular file, is a final-component reparse point, is denied/locked, or raises a controlled read I/O failure. The safe reason code is evidence; raw exception text is logged only at an appropriate technical level without book content.
- `FORMAT_FILE_CHANGED_DURING_HASHING`: observable state changed between preflight, open, end-of-stream, and post-read validation. The digest is discarded and the file is excluded from grouping.
- Existing `MANAGED_PATH_INVALID` remains the result for a rejected Calibre-managed path; such a path is never sent to the hasher.

Findings are ordered by book ID, format, relative path, and code. Multiple observations for the same format/code are deduplicated. A later disappearance after path resolution produces one missing finding, not a fatal scan error.

### Streaming SHA-256 implementation

Add `StreamingSha256FormatFileHasher` in Infrastructure:

- Use `FileStreamOptions` with `Mode = FileMode.Open`, `Access = FileAccess.Read`, `Share = FileShare.Read`, a fixed 128 KiB buffer, and `FileOptions.Asynchronous | FileOptions.SequentialScan`. A sharing conflict is a per-file inaccessible finding. Excluding writers and replacement for the lifetime of the read is the smallest reliable correction for Windows file timestamps that may remain stale while a writer handle is open.
- Never use `File.ReadAllBytes`, `ReadToEnd`, a memory-mapped whole-file view, a temporary copy, or any create/write mode.
- Rent one buffer per active worker from `ArrayPool<byte>.Shared`, return it in `finally`, and feed each `ReadAsync` chunk to `IncrementalHash.CreateHash(HashAlgorithmName.SHA256).AppendData(...)`.
- Call `GetHashAndReset`/equivalent only after EOF and state validation; canonicalize with lowercase hexadecimal text.
- Dispose the stream and incremental hash on success, expected failure, unexpected failure, and cancellation. Do not retry automatically, because a retry can hide an unstable file and distort progress.
- Read empty files successfully and assign the standard SHA-256 of zero bytes with size zero.
- Use only BCL APIs; no package or native hashing dependency is added. The implementation choices are supported by Microsoft's .NET 10 documentation for [`FileStreamOptions`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestreamoptions?view=net-10.0), [`FileOptions`](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileoptions?view=net-10.0), and [`IncrementalHash`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.incrementalhash?view=net-10.0).

### Bounded-concurrency design

- Preallocate a result slot for every request and process eligible entries with `Parallel.ForEachAsync` (or an equivalent fixed worker loop) configured with `MaxDegreeOfParallelism = LibraryAnalysisOptions.MaxHashConcurrency`.
- Default to four concurrent files. Do not derive an unbounded value from catalog size. Tests override the limit with small deterministic values.
- Keep request/result metadata O(number of formats); only active workers own streams, incremental hash state, and rented buffers.
- Serialize aggregate progress updates through a small coordinator. Use atomic counters for bytes/files and a lock only around throttling/report emission; never serialize the actual file reads.
- Report initially, at bounded byte/file intervals, and at phase completion. Do not report every file start/completion; WPF progress delivery must not enqueue work proportional to every tiny file faster than the dispatcher can consume it.
- Preserve result order by request sequence, not task completion. Duplicate detection therefore never depends on scheduling.

### Progress reporting

- Retain existing validation/database/path phases.
- `HashingFormats` reports aggregate bytes when at least one preflighted file has a positive length. Its message also reports completed files out of total resolved files, including classified failures.
- If all eligible files are zero length, use completed-file units so the phase remains determinate.
- Totals are fixed after preflight. A changed file may make the byte total stale; cap displayed completed bytes at the planned total and complete the phase by file count rather than pretending the discarded hash succeeded.
- `GroupingExactDuplicates` reports `0/1` and `1/1`; grouping is in-memory and normally brief but still checks cancellation.
- `Completed` is reported exactly once only after the immutable snapshot and groups exist.
- `MainWindowViewModel` continues to create `Progress<LibraryScanProgress>` on the dispatcher. Within each phase, completed units never decrease and never exceed totals.

### Cancellation behavior

- Check the token before/after validation, database read, path resolution, hashing preflight, each worker schedule, every `ReadAsync`, result mapping, group construction, and final snapshot commit.
- Canceling stops new file work and signals all active reads. The use case awaits active worker disposal and then propagates `OperationCanceledException`.
- A 128 KiB read buffer bounds cooperative cancellation latency without excessive syscall overhead. Cancellation during a blocking OS read may still wait for that read to return; document and test the practical behavior with synthetic large files.
- Cancellation is neither a finding nor an error. No partial hashes/groups are committed to WPF, and a later scan can run normally.
- The scanner never truncates, locks exclusively, deletes, or writes a file as part of cancellation.

### File-change detection during hashing

For each resolved path, Infrastructure captures a best-effort read state containing regular-file/reparse attributes, length, creation time, and last-write time. It then:

1. Classifies absence before open as `Missing` and access denial/sharing/unsafe type as `Inaccessible`.
2. Rejects a final-component reparse point so an already validated directory path cannot redirect hashing outside the library.
3. Revalidates every parent component from the trusted library root, rejects reparse points, and opens the exact path read-only without sharing writes or deletion.
4. Compares the opened stream length and immediate path state with preflight state before hashing.
5. Streams exactly to EOF while counting bytes.
6. Rechecks stream length and path state after reading and before accepting the digest.
7. Returns `ChangedDuringHashing` and discards the digest if the path disappeared/replaced, length changed, timestamps/attributes changed, or bytes read do not equal the accepted initial length.

This detects ordinary concurrent edits, replacement, truncation, and growth without locking or modifying the library. It cannot prove that a writer changed bytes and restored the same length/timestamps during the read. That residual race is documented as a risk; adding Windows file IDs or a second full hash is not proposed for Milestone 2.

### Missing and inaccessible files

- Missing at preflight/open is non-fatal and yields `FORMAT_FILE_MISSING` with book ID, format, and relative path.
- `UnauthorizedAccessException`, `SecurityException`, sharing violations, a directory at the expected path, a final reparse point, and controlled per-file `IOException` cases yield `FORMAT_FILE_INACCESSIBLE` with a stable reason code.
- A file disappearing after it was opened is `ChangedDuringHashing`, because the scan observed instability rather than simple initial absence.
- Do not enumerate sibling files, repair case, adopt an alternate file, or retry with a mutable share/access mode.
- Do not include raw exception messages in findings; structured logs may include exception type, safe reason code, phase, and counts, but not title/author/identifier/content.

### Duplicate-group identity, equality, and ordering

- Hash every eligible existing file. Do not skip singleton sizes: the product must retain a hash for every successfully read format, and later milestones/caching may need that complete fingerprint.
- Use size as a cheap first partition and SHA-256 as the second key. A group exists only when both values match and at least two distinct managed format references are present.
- Same size with different SHA-256 is not a duplicate. SHA-256 equality with different size is not grouped, even though such a pair is extraordinarily unlikely.
- Group identity derives only from `(SHA-256, size)` and is stable across scans/member ordering.
- Sort members by book ID ascending, then canonical format ordinal, then normalized relative path ordinal.
- Sort groups by file size descending, then digest ordinal. This is deterministic and surfaces the largest redundant byte sets first without making a recommendation.
- Expose member count and distinct-record count. A group spanning multiple records is still labeled an exact file group, not a duplicate-record verdict.
- Do not calculate recoverable-space totals, choose a keeper, rank records, or mark any member safe to delete.

### WPF ViewModels and minimal views

- Extend `FormatRowViewModel` with file size, full SHA-256 text (copyable), and statuses for inaccessible/changed files. Missing/invalid/inaccessible/changed status must remain text, not color-only.
- Add `ExactDuplicateGroupRowViewModel` with stable ID, formatted size, SHA-256, member count, distinct record count, and an explicit `File duplicates` label.
- Add `ExactDuplicateMemberRowViewModel` with record ID, title, authors, format, and expected relative path. Display metadata is joined from snapshot books; the Domain group does not duplicate it.
- Extend `MainWindowViewModel` with a read-only groups collection, selected group, selected members, group count, and an empty-state explanation. Results are atomically replaced only on successful scan.
- Add a `TabControl` or equivalent split layout: retain the existing `Library` book/format view and add `Exact file duplicates`. The duplicate tab uses a group grid beside/above a member grid so selecting a group shows all involved records/formats.
- Include static explanatory text: `These groups contain byte-identical files. They do not by themselves identify duplicate book records.` Do not use labels such as merge candidates, redundant records, keep, delete, or recommendation.
- Show hashing/grouping progress through the existing progress bar/status text and retain Browse/Scan/Cancel command behavior.
- Keep ViewModels independent of Infrastructure. `App.xaml.cs` remains the only WPF composition-root reference to Infrastructure and registers `LibraryAnalysisOptions` if Infrastructure DI does not provide the default.
- Keep `MainWindow.xaml.cs` limited to view initialization. Preserve keyboard access, readable labels, automation names, resize behavior, and high-DPI support.

## Files expected to change

Deviations from this concrete set must be recorded in this plan before implementation continues.

### Documentation

- `docs/plans/milestone-2-hashing-and-exact-duplicates.md` — keep progress, decisions, deviations, verification results, and final outcome current.
- `docs/domain-model.md` — add fingerprint/exact-binary types and refine file-group versus record-group invariants.
- `docs/duplicate-detection.md` — clarify exact binary group evidence, equality key, deterministic ordering, and non-equivalence to duplicate records.

No package-version or project-reference change is expected; SHA-256 uses the .NET BCL.

### Domain

- `src/CalibreLibraryCleaner.Domain/Libraries/FormatFileStatus.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/BookFormat.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/LibrarySnapshot.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/Sha256Digest.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Libraries/FormatFileFingerprint.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactBinaryDuplicateGroupId.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactBinaryDuplicateMember.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactBinaryDuplicateGroup.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactBinaryDuplicateDetector.cs` — new.

### Application

- `src/CalibreLibraryCleaner.Application/Abstractions/ILibraryPathResolver.cs` — remove the lossy existence check.
- `src/CalibreLibraryCleaner.Application/Abstractions/IFormatFileHasher.cs` — new batch hashing port.
- `src/CalibreLibraryCleaner.Application/Libraries/FormatHashContracts.cs` — new provider-neutral requests/results/progress statuses.
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryAnalysisOptions.cs` — new validated bounded-concurrency policy.
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanPhase.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryErrorCode.cs` — only if a distinct fatal hashing error is needed.
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs`

`ValidateLibraryUseCase`, SQLite catalog contracts, and schema contracts are not expected to change.

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Paths/LibraryPathResolver.cs` — remove `FileExistsAsync`; retain validation/resolution rules.
- `src/CalibreLibraryCleaner.Infrastructure/Hashing/StreamingSha256FormatFileHasher.cs` — new streaming, state-checking, bounded batch implementation.

`ReadOnlySqliteConnectionFactory`, `CalibreSchemaContract`, `CalibreSchemaInspector`, and `SqliteCalibreMetadataReader` are not expected to change. No writable repository, cache, temp-copy service, ebook parser, or Calibre process wrapper is added.

### WPF

- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs` — register the analysis policy/use-case dependencies if required.
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/FormatRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/ExactDuplicateGroupRowViewModel.cs` — new.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/ExactDuplicateMemberRowViewModel.cs` — new.

### Tests and synthetic fixtures

- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibrarySnapshotTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibraryValueTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Duplicates/ExactBinaryDuplicateDetectorTests.cs` — new.
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ScanLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticCalibreLibrary.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/LibraryStateCapture.cs` — stream test manifest hashes.
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/TestServices.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Paths/LibraryPathResolverTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Hashing/StreamingSha256FormatFileHasherTests.cs` — new.
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Safety/ReadOnlyLibraryScanSafetyTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/MainWindowTests.cs`

Existing metadata-reader/schema tests should remain unchanged except for fixture API adjustments that are mechanically required.

## Safety considerations

- `metadata.db` continues to use the existing strict read-only connection and query-only guard. Hashing never opens the database and introduces no SQL.
- Production file streams use only `FileMode.Open` and `FileAccess.Read`. No create, append, truncate, write, copy, move, replace, rename, delete, attribute mutation, timestamp mutation, lock-file, sidecar, cache, or temp file is permitted inside the library.
- The hasher receives only paths already canonicalized and contained by `LibraryPathResolver`; it rechecks the final file type/reparse attribute before opening.
- Read-only sharing fails closed when Calibre or another process is writing/replacing a file. A sharing conflict is excluded rather than retried or accepted, and parent components are revalidated immediately before and after open.
- Missing/inaccessible/changed formats cannot enter duplicate groups because they have no accepted fingerprint.
- Cancellation discards partial digest state and produces no domain result or filesystem artifact.
- Safety manifests cover the entire synthetic library before/after and use streaming test hashes. Tests assert database bytes, ebook bytes, names, entry counts, lengths, attributes, and timestamps are unchanged and no `metadata.db-*` files appear.
- Synthetic tests may deliberately lock or mutate only test-owned files to exercise failure detection. Such test actions must be isolated from assertions about scanner-caused mutation and cleaned up before fixture disposal.
- Logs contain counts, phases, exception types, and stable reason codes only; no ebook bytes, title/author/identifier content, full hash-associated content, or raw user-facing exception details are logged by default.
- The complete production diff must be scanned for mutable filesystem APIs, SQL mutation verbs, hashing caches, EPUB/PDF readers, scoring, recommendations, cleanup, and Calibre CLI behavior.

## Implementation steps

1. Record approved answers to the unresolved questions below and update this plan's Progress section before editing production code.
2. Add Domain digest/fingerprint/file-group values, invariants, deterministic detector, and focused tests. Update domain/duplicate documentation with the file-versus-record distinction.
3. Add Application hashing contracts/options and remove `FileExistsAsync` from the path port.
4. Refactor `ScanLibraryUseCase` to collect safe requests, call the batch hasher, map results/findings, construct fingerprinted formats, invoke the Domain detector, and report the two new phases.
5. Extend synthetic fixture helpers to create multiple books/formats with caller-supplied streamed content, same-size/different-content files, empty files, large files, locked files, and files changed by a test during hashing.
6. Implement `StreamingSha256FormatFileHasher` with read-only async streams, pooled fixed-size buffers, incremental SHA-256, bounded concurrency, deterministic result slots, throttled progress, expected exception mapping, and state validation.
7. Register the hasher and explicit default concurrency policy through DI/composition. Do not add a package.
8. Add Infrastructure integration tests for known hashes, chunk boundaries, empty/large files, bounded concurrency, progress, cancellation, missing/inaccessible/reparse/changed classification, deterministic result ordering, and released handles.
9. Extend Domain/Application tests for exact grouping, same-size non-matches, finding mapping, progress, cancellation, and no partial snapshot.
10. Extend the WPF ViewModels and XAML with exact-file-group and member views, explanatory labeling, empty state, selection behavior, progress, and cancellation coverage.
11. Extend architecture tests to keep cryptographic/file APIs in Infrastructure and keep WPF ViewModels independent of Infrastructure/filesystem types.
12. Extend safety tests for successful duplicate analysis, singleton/non-match analysis, missing/inaccessible files, and cancellation. Preserve separate controlled tests where the fixture itself changes a file to trigger change detection.
13. Run focused tests while iterating, then the standard restore/build/test/format sequence and package vulnerability audit.
14. Run a WPF process startup/graceful-close smoke test and, where possible, an interactive smoke test against a generated synthetic duplicate library only.
15. Review the complete staged/unstaged diff, package graph, production SQL, filesystem API surface, and future-scope terms. Verify no library mutation code was introduced.
16. Update Progress, deviations/failed approaches, command results, safety evidence, remaining risks, and Final outcome.

## Tests

### Domain unit tests

- `Sha256Digest` accepts canonical 64-character hex, normalizes approved uppercase input if supported, and rejects blank, wrong-length, or non-hex values.
- `FormatFileFingerprint` accepts zero length and rejects negative length.
- `BookFormat` enforces fingerprint/status consistency and remains immutable.
- An exact group requires at least two distinct managed format references and rejects duplicate members.
- Equal `(size, SHA-256)` formats group; equal size/different digest and equal digest/different size do not.
- Missing/inaccessible/changed/invalid formats never group.
- Shuffled books/formats produce the same group IDs, group order, and member order.
- Same-record distinct managed files follow the approved policy and expose `DistinctBookCount` correctly.
- Snapshot/group/member constructors defensively copy collections.

### Application unit tests

- Invalid library/catalog/path failures do not call the hasher.
- Every safely resolved format, including same-size singletons, is sent once to the hasher; invalid paths are not.
- The configured concurrency bound and cancellation token are passed unchanged.
- Successful results produce present formats with fingerprints and exact groups.
- Missing, inaccessible, and changed results produce the exact structured finding/status and do not abort unrelated formats.
- Result mapping is by request sequence/identity rather than completion order.
- Duplicate groups and findings remain deterministic when the fake hasher returns shuffled completion data.
- Hashing and grouping phases report monotonic progress and `Completed` exactly once.
- Cancellation during hashing or grouping propagates, suppresses `Completed`, and produces no partial successful outcome.
- A fatal hashing-batch failure becomes the defined actionable `LibraryError` without raw exception text.

Use FakeItEasy for ports/options where appropriate and FluentAssertions for results/calls.

### Infrastructure integration tests

- Known synthetic byte sequences, empty files, and content crossing multiple 128 KiB chunks produce known SHA-256/size results.
- A multi-megabyte synthetic file hashes successfully without `ReadAllBytes`; a source/API guard confirms production hashing contains no whole-file read API.
- A configured concurrency limit is never exceeded and at least two workers overlap when the limit/fixture permits; progress exposes enough active/completed state to assert this without timing-only sleeps.
- Aggregate byte/file progress is monotonic, bounded, throttled, and completes for empty/mixed-size sets.
- Pre-cancellation, cancellation during a large read, and cancellation with multiple active workers throw promptly and release every handle/buffer.
- A missing file, directory at file path, exclusive-lock sharing violation, access denial where deterministically supported, and final reparse point receive the intended statuses.
- A test-owned file grown, truncated, replaced, or removed after hashing begins produces `ChangedDuringHashing` and no fingerprint.
- Results retain request order when larger files finish after smaller later requests.
- File handles are released after success/failure/cancellation, proven by renaming/deleting only the test-owned fixture after the operation.
- Existing read-only SQLite/schema/path integration tests continue to pass.

Avoid flaky wall-clock performance assertions. Concurrency tests should use progress/gates or controlled locks, not arbitrary delays. OS-specific access/reparse cases may be conditional only when the platform cannot create the required synthetic state; core missing/locked/changed behavior must remain mandatory on Windows.

### WPF/ViewModel tests

- Successful scan displays deterministic exact-file groups and automatically selects the first group.
- Selecting a group displays every involved record and format with the correct record ID/title/authors/path.
- The UI exposes file count and distinct-record count and includes text stating that file equality is not a duplicate-record verdict.
- Same-size/different-hash files and singleton hashes produce the exact-duplicates empty state.
- Missing/inaccessible/changed statuses remain visible in the library format view and do not appear as duplicate members.
- Hashing progress updates status/percentage without blocking; Cancel remains enabled until cancellation is requested.
- Cancellation retains previous results, shows a neutral status, and allows retry.
- `MainWindow` still constructs/shows/closes on STA so new bindings/templates activate without startup exceptions.

Manual XAML review covers keyboard tab order, group/member navigation, labels/access keys, copyable hash text, resizing, high DPI, screen-reader automation names, and status distinctions that do not rely on color.

### Architecture tests

- Existing project-reference direction remains unchanged.
- Domain and Application declare/reference no SQLite, filesystem, WPF, logging/DI implementation, or ebook-parser package.
- Domain/Application production source does not use `System.IO`, `FileStream`, `IncrementalHash`, or `SHA256` implementation APIs.
- `System.IO` and `System.Security.Cryptography` implementation usage for product hashing is confined to Infrastructure.
- WPF ViewModels reference only Application/Domain/presentation types and no Infrastructure, filesystem, or crypto implementation.
- WPF's Infrastructure namespace use remains confined to `App.xaml.cs`.

### Safety tests

- Capture the recursive synthetic library manifest before/after successful hashing with exact groups and assert strict equality of every entry.
- Repeat non-mutation assertions for same-size nonmatches, missing files, inaccessible/locked files, fatal read failure, and cancellation.
- Assert `metadata.db` bytes/hash and timestamps are unchanged and no journal/WAL/SHM/cache/temp file appears.
- Assert format bytes, size, timestamps, attributes, names, and locations are unchanged.
- Assert the scanner never creates a hash cache or writes hash data back into SQLite/files.
- Keep controlled changed-file tests separate: record that the test actor mutated the test-owned file and assert the scanner performed no additional mutation.
- Search production source for SQL mutation verbs; `FileMode` create/append/truncate; `File.Write*`, `File.Create`, `File.Delete`, `File.Move`, `File.Copy`, `File.Replace`; directory create/delete/move; Calibre CLI; EPUB/PDF libraries; scoring/recommendation/cleanup types.

## Performance and memory considerations

- Each active worker uses one 128 KiB rented buffer plus small `FileStream`/hash state. At the default concurrency of four, hashing buffers are approximately 512 KiB total, independent of file sizes.
- Requests, results, fingerprints, and grouping dictionaries are O(number of declared formats). Ebook contents are never retained.
- Hashing is I/O-bound. Four workers is a conservative default that can overlap storage latency without opening an unbounded number of files or aggressively thrashing one disk.
- Size partitioning makes grouping O(number of hashed formats) expected time and avoids pairwise O(n²) byte/hash comparisons.
- SHA-256 is still calculated for singleton sizes by design; this costs I/O but fulfills the complete-hash requirement and avoids inconsistent snapshots.
- Progress is emitted at bounded byte intervals and file boundaries to keep dispatcher work small for tens of thousands of formats.
- Integration tests should include many small files and a few large synthetic files. Record elapsed time and peak working-set observations only as diagnostics, not brittle pass/fail thresholds.
- A future persistent cache could avoid rehashing unchanged files, but it is explicitly deferred because cache location, invalidation, privacy, and safety need separate design.

## Verification commands

Run focused tests during implementation:

```powershell
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj --filter "FullyQualifiedName~ExactBinary|FullyQualifiedName~Fingerprint|FullyQualifiedName~Sha256"
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --filter "FullyQualifiedName~ScanLibraryUseCase"
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Hash|FullyQualifiedName~Safety"
dotnet test tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj
```

Then run the required full sequence:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
dotnet list package --vulnerable --include-transitive
```

Review repository and safety surfaces:

```powershell
git status --short
git diff --check
git diff --stat HEAD
git diff HEAD
rg -n --glob 'src/**/*.cs' 'CommandText\s*=|SqliteOpenMode|query_only' src
rg -n --glob 'src/**/*.cs' -e 'File\.(Write|Delete|Move|Copy|Create|OpenWrite|Replace)' -e 'Directory\.(Create|Delete|Move)' -e 'FileMode\.(Create|Append|OpenOrCreate|Truncate)' -e '\b(INSERT|UPDATE|DELETE|DROP|ALTER|REPLACE|VACUUM|ATTACH|DETACH)\b' -e 'calibredb|VersOne|PdfPig|recommendation|cleanup plan' src
```

Start the built WPF executable, verify it remains alive through startup, request graceful close, and require empty standard error. If an interactive session is available, select only a generated synthetic library and verify hashing progress, cancellation, group/member selection, empty state, explanatory text, resize/high-DPI, and keyboard behavior.

Do not report a command as successful until it actually completes. Record exact test counts, warnings, failures, process-smoke results, mutation-scan results, and any manual limitations in Progress and Final outcome.

## Risks

- Filesystem metadata checks cannot detect a writer that changes bytes and restores the same length/timestamps during the read. The accepted digest is a best-effort stable observation, not a filesystem transaction.
- `FileShare.Read` prevents a file from changing or being replaced while it is hashed, but a concurrent Calibre/synchronization writer will receive or cause a sharing conflict. The scan fails that file closed as inaccessible and asks the user to retry after writing finishes.
- Cancellation cannot guarantee interruption inside every kernel/storage-driver read; bounded chunks and async reads minimize application-side latency.
- Four concurrent sequential readers can help SSD/network storage but may reduce throughput on a single spinning disk. The bound is explicit and testable, but user tuning is deferred.
- Preflight totals can become stale if files change before opening. Progress must remain bounded and honest rather than silently revising accepted fingerprints.
- SHA-256 collisions are theoretically possible. Requiring equal size as well reduces accidental grouping risk, but this milestone does not perform byte-for-byte second-pass comparison.
- Exact file equality does not establish duplicate records, editions, or safe deletion. UI wording is a safety control, not cosmetic text.
- Very large libraries retain O(format count) request/result/group metadata in memory. They do not retain file contents, but large synthetic scale tests are still needed.
- Windows reparse points, sharing violations, antivirus filters, network shares, timestamp precision, and replacement semantics can vary. Tests must avoid weakening the fail-closed policy for platform convenience.
- Progress callbacks from concurrent workers can arrive rapidly or out of order unless explicitly serialized and throttled.
- The current working tree is already dirty from completed Milestone 1/user/IDE changes. Diff review must separate Milestone 2 edits and preserve unrelated files.

## Unresolved questions

1. Should two byte-identical managed files attached to the same Calibre record form an exact binary file group? Proposed answer: yes, if their `(BookId, Format, RelativePath)` references are distinct. Report `DistinctBookCount = 1`; do not call it a duplicate-record group. Update the generic domain-model invariant accordingly.
2. What default hash concurrency should be used? Proposed answer: four, explicitly registered and test-overridable. Do not expose a settings screen in Milestone 2.
3. Should same-size singleton partitions skip SHA-256? Proposed answer: no. Hash every safely readable existing format; size is a grouping pre-filter only.
4. What buffer/progress interval should be used? Proposed answer: a 128 KiB pooled buffer per worker and aggregate progress at 4 MiB/file boundaries, subject to measurement with synthetic fixtures.
5. Should the scanner open with restrictive sharing to prevent changes? Final answer after review: yes. Use `FileShare.Read` so a live writer or replacement produces an inaccessible result rather than relying on timestamps that Windows may not update until writer handles close. Ask the user to wait for Calibre/synchronization and retry.
6. Is length/creation-time/last-write-time/attributes plus pre/post stream state sufficient for Milestone 2? Proposed answer: yes, with the same-metadata concurrent rewrite limitation documented. Native file-ID tracking and double hashing are deferred.
7. Should exact groups require a byte-for-byte second comparison after matching size/SHA-256? Proposed answer: no for Milestone 2; the requirement defines exact binary confidence by SHA-256, and a second full read doubles I/O and expands change windows.
8. Should hashes be persisted outside the library? Proposed answer: no. Keep them only in the in-memory snapshot; caching is a future independently planned capability.

These defaults keep the slice deterministic and read-only. Any answer that expands into record matching, persistence, mutation, parsing, scoring, or cleanup is outside Milestone 2 and requires a separate plan.

Approved resolution: all proposed answers above were accepted for implementation on 2026-07-14.

Review correction approved on 2026-07-14: the completed implementation review found that permissive sharing plus metadata comparison could accept a same-length write while the writer remained open. The smallest safety correction supersedes unresolved-question answer 5 as recorded above. The same review also requires parent-component revalidation at hashing time, cancellation inside grouping/final materialization, coalesced progress and bulk UI updates, sanitized expected-failure logs, complete hash-result validation, and focused tests for these behaviors.

## Progress

- [x] Read root `AGENTS.md` and every nested `AGENTS.md` found under `src/` and `tests/`.
- [x] Read `PLANS.md`.
- [x] Read the requested product vision, functional requirements, architecture, domain model, duplicate detection, test strategy, and roadmap documents.
- [x] Read every accepted ADR under `docs/adr/`.
- [x] Read the completed Milestone 0 and Milestone 1 plans, including recorded deviations and final outcomes.
- [x] Inspect the current solution/project configuration, all production Domain/Application/Infrastructure/WPF source and XAML, relevant tests/fixtures, architecture tests, and working-tree state.
- [x] Verify the proposed streaming primitives against the official .NET 10 API surface.
- [x] Create the Milestone 2 execution plan.
- [x] Resolve and record the unresolved design questions.
- [x] Implement Domain hashing/grouping values and tests.
- [x] Implement Application contracts/orchestration and tests.
- [x] Implement Infrastructure streaming/bounded hashing and tests.
- [x] Implement WPF exact-file-group display and tests.
- [x] Complete architecture and non-mutation safety coverage.
- [x] Run focused and full verification commands.
- [x] Review the complete diff and production mutation/future-scope surface.
- [x] Update Final outcome.
- [x] Apply the completed-implementation review corrections without expanding Milestone 2 scope.
- [x] Re-run focused/full verification and record the corrected final outcome.

Implementation notes:

- The first production compile failed on analyzer CA1859 for an internal dictionary parameter; the concrete type was used as requested without suppressing the analyzer.
- Subsequent compile attempts exposed expected test call-site changes after removing the lossy `FileExistsAsync` port and adding the hashing dependency. Tests were migrated to structured hash outcomes; no compatibility shim was retained.
- Focused hashing/safety tests were run three consecutive times after implementation to check cancellation/change-detection stability; all three runs passed.
- The original completion run found a pre-existing `.gitignore` blank-line warning and tracked Rider workspace churn. The requested review-correction pass removed the blank-line problem, ignored `.idea`, and removed the tracked `.idea/workspace.xml`; final `git diff --check HEAD` passed.
- The completed-implementation review found eight issues. Corrections use strict read sharing, trusted-root and parent-component revalidation, cancellation-aware grouping/final mapping, coalesced WPF progress, bulk collection reset, sanitized logging, complete result-contract validation, expanded tests, and removal of tracked Rider workspace state. No future milestone functionality was added.
- The first correction build exposed analyzer CA1859 on the internal hash-result validator; the concrete `List<FormatHashRequest>` type was used without suppression. A later correction build exposed test-only expression-tree and missing-namespace errors, which were fixed directly. `dotnet format --verify-no-changes` then exposed LF line endings introduced by patching; `dotnet format` normalized them and the final verification passed.
- Three consecutive focused hashing/safety runs passed after the corrections (20 tests per run).

## Final outcome

Completed on 2026-07-14.

- Added immutable SHA-256 digest/file-fingerprint values, exact binary file group/member identities, deterministic grouping, and the explicit distinction between identical files and duplicate book records.
- Extended the scan use case to hash every safely resolved format, map missing/inaccessible/changed files to structured findings, discard stale hashes, group matching `(size, SHA-256)` fingerprints, and commit only a complete snapshot.
- Added an Infrastructure-only streaming hasher using read-only asynchronous `FileStream`, a rented 128 KiB buffer per active worker, `IncrementalHash` SHA-256, strict read sharing, trusted-root/parent-chain checks, observable pre/post file-state checks, deterministic result slots, aggregate throttled progress, and `Parallel.ForEachAsync` bounded by the explicitly registered default of four workers.
- Removed the lossy `File.Exists` Application port. Infrastructure now distinguishes initial absence, access/locking failures, and files that change or disappear after preflight.
- Added WPF library-format size/hash/status columns and an Exact file duplicates tab with group and member grids, record/file counts, and explicit text that byte-identical files do not by themselves identify duplicate book records.
- Added/updated synthetic Domain, Application, Infrastructure, architecture, WPF, and full-library safety tests. Coverage includes known and empty hashes, multi-buffer streaming, size/hash grouping, same-record file groups, deterministic ordering, missing and exclusively locked files, changes during hashing, bounded active workers, cancellation, exact-group ViewModels, unchanged recursive library manifests, and no SQLite sidecars.
- Test manifest hashing was changed to stream so large synthetic fixtures do not make the safety harness itself use whole-file reads.
- No package or project-reference change was required; SHA-256 uses the .NET 10 BCL.
- Final `dotnet restore` succeeded.
- Final `dotnet build --no-restore` succeeded with zero warnings and zero errors.
- Final `dotnet test --no-build` succeeded: 72 passed, 0 failed, 0 skipped across Domain (11), Application (12), Infrastructure (32), Architecture (11), and WPF (6).
- Final `dotnet format --verify-no-changes` succeeded.
- `dotnet list package --vulnerable --include-transitive` reported no known vulnerable packages in any project.
- Final WPF startup/graceful-close smoke test remained alive through startup, accepted `CloseMainWindow`, exited within five seconds, and produced empty standard error. PowerShell returned a null `ExitCode` for the redirected process even after waiting, so numeric exit-code verification remains inconclusive. An interactive synthetic-library visual/high-DPI/accessibility review remains manual follow-up.
- Complete product diff, SQL, architecture, cryptography-boundary, whole-file-read, mutation, and future-scope scans found no writable SQLite mode/statement, mutable production filesystem call, whole-file production read, hashing outside Infrastructure, title/author matching, ebook inspection, scoring, recommendation, cleanup, backup, rollback, merge, deletion, or Calibre CLI implementation.
- Deviations: the completed review superseded permissive sharing with `FileShare.Read`, added the trusted library root to the provider-neutral resolved-path contract, coalesced WPF progress, and removed tracked Rider workspace state. The batch port, four-worker default, 128 KiB buffers, 4 MiB byte interval, in-memory-only hashes, and same-record file-group policy remain unchanged.
- Remaining risks: synchronous metadata/open calls cannot be interrupted inside every kernel/storage-driver operation; four concurrent reads may be suboptimal on some storage; SHA-256 collision risk is theoretical but non-zero; linked/reparse paths fail closed; strict sharing can temporarily conflict with Calibre writers; and exact file equality still does not establish record equivalence or safe deletion.
