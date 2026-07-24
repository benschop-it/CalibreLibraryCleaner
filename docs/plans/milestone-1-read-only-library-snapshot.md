# Milestone 1 Read-Only Library Snapshot

## Objective

Deliver the first end-to-end, read-only vertical slice of Calibre Library Cleaner. A user can choose a Calibre library folder in the WPF application, validate it, scan its `metadata.db` without modifying the database or library, and see the books and declared formats. Missing expected format files are retained in the snapshot and shown as findings rather than causing the scan to fail. The operation remains responsive, reports progress, supports cancellation, and presents actionable validation and scan errors.

The milestone is complete only when the behavior is exercised with synthetic Calibre-style SQLite/filesystem fixtures and safety tests prove that scans do not change anything inside the selected library.

## Scope

- Select a folder through a WPF folder picker.
- Validate non-empty input, canonicalize the selected folder, and verify that the folder and its top-level `metadata.db` are readable files.
- Open `metadata.db` through SQLite's read-only mode, add a query-only guard, and issue only documented `SELECT` and `PRAGMA` statements.
- Validate the required Calibre schema before reading catalog data.
- Load the Calibre library UUID/schema version, books, book-level author-sort values, ordered authors and their individual sort values, identifiers, formats, stored format basenames, and Calibre-managed book-relative directories.
- Derive each expected format filename and library-relative path without renaming or searching for substitutes.
- Check whether each expected format file exists without opening its content.
- Produce a non-fatal finding for every missing expected format file or unsafe/anomalous managed path that can be attributed to a book/format.
- Produce an immutable, deterministically ordered `LibrarySnapshot` for successful scans.
- Show a minimal master/detail WPF view: books in one list and the selected book's formats in another.
- Report phased progress, permit cancellation throughout validation, database reading, mapping, and path checks, and leave the UI usable after success, failure, or cancellation.
- Show concise error text plus a suggested user action for expected validation and scan failures.
- Add the packages, dependency registrations, tests, synthetic fixtures, and documentation updates required by this slice.

## Out of scope

- Hashing or reading format-file content.
- Binary, identifier, title/author, content, or fuzzy duplicate detection.
- EPUB or PDF parsing, validation, metadata extraction, or scoring.
- Series, languages, covers, tags, custom columns, comments, ratings, or other Calibre metadata not explicitly needed by this slice.
- Quality scoring, recommendations, comparison/review workflows, overrides, or defer state.
- Cleanup plans, approval, backups, Calibre CLI mutation, execution, verification, audit, or rollback.
- Creating, renaming, overwriting, moving, deleting, repairing, or normalizing anything in a selected library.
- Falling back to another same-extension file when the exact database-derived format path is missing. Calibre's own mutable fallback/rename behavior must not be copied.
- Persisting settings such as the last selected folder.
- Scanning more than one library at a time.
- Depending on a user's installed Calibre, a user's real library, network access, or cloud/AI services in automated tests.
- Broad domain scaffolding for future milestones.

## Relevant requirements

- `AGENTS.md`: analysis is read-only, paths are validated/canonicalized, long-running work is asynchronous/cancellable/progress-reporting, expected problems use structured findings, tests use synthetic fixtures, and no future milestone is implemented.
- `PLANS.md`: this cross-project and safety-sensitive change follows the required execution-plan structure and must be kept current during implementation.
- `docs/product-vision.md`: safety, local-first operation, deterministic explanations, and no direct database mutation take priority.
- `docs/functional-requirements.md`: select/validate a library, read `metadata.db`, load the requested catalog fields and managed paths, resolve format files, and report missing/anomalous paths.
- `docs/architecture.md` and ADR 0002: preserve `Domain <- Application <- Infrastructure` and `Application <- Wpf`; WPF may use Infrastructure only in composition.
- `docs/domain-model.md`: introduce the Milestone 1 subset of `LibrarySnapshot`, `LibraryIdentity`, `CalibreBook`, `BookFormat`, and `LibraryFinding` as immutable domain values. Do not add future duplicate, assessment, recommendation, or plan types.
- `docs/test-strategy.md`: cover validation, cancellation, missing files, read-only SQLite, paths, UI behavior, architecture boundaries, and safety with generated temporary fixtures.
- `docs/roadmap.md`: implement Milestone 1 only; hashing begins in Milestone 2.
- ADR 0001: enforce read-only SQLite access, isolate schema mapping, detect mutations in tests, surface unsupported schemas, and provide no SQL-write path.
- ADR 0003: no Calibre CLI or mutation integration is introduced.
- Nested project/test `AGENTS.md` files: Domain remains integration-free, Application owns use cases and ports, Infrastructure contains SQLite/filesystem details, WPF uses MVVM, and all tests are deterministic/offline.

## Existing implementation inspected

- Milestone 0 is complete at commit `5bc9f7e`; its recorded restore, build, test, and format checks succeeded.
- `CalibreLibraryCleaner.sln` contains the four documented production projects and four current test projects.
- `Directory.Build.props` targets .NET 10, enables nullable references, latest C#, deterministic builds, recommended analyzers, code-style enforcement, and warnings as errors.
- `Directory.Packages.props` centrally versions CommunityToolkit.Mvvm, hosting/DI/logging abstractions, xUnit, FakeItEasy, FluentAssertions, and the test SDK. It does not yet include a SQLite provider.
- Domain, Application, and Infrastructure currently contain only `AssemblyMarker.cs`; no product behavior or domain model exists.
- Infrastructure references Application and has DI/logging abstractions, but no composition extension or integration implementation exists.
- WPF is a blank 800x450 `MainWindow`, uses `StartupUri`, and has only view initialization in code-behind. It already references Application and Infrastructure and packages CommunityToolkit.Mvvm and `Microsoft.Extensions.Hosting`.
- Current tests consist of three assembly smoke tests and `DependencyDirectionTests`. There is no WPF-focused test project yet.
- The architecture tests inspect project references, prohibit integration packages in Domain/Application, and prohibit integration/UI assembly references from Domain.
- The working tree contained unrelated untracked Rider `.idea` metadata before this plan; implementation must leave those files untouched.

## Proposed design

### End-to-end flow

1. `MainWindowViewModel.SelectLibraryCommand` invokes a WPF-only `ILibraryFolderPicker`. Canceling the picker leaves the current selection unchanged.
2. The selected text is passed to `ValidateLibraryUseCase`. Validation returns either a `ValidatedLibraryLocation` containing canonical root/database paths or a structured `LibraryError`; expected validation failures do not throw.
3. `ScanLibraryUseCase` revalidates immediately before every scan so a previous validation cannot become a time-of-check/time-of-use bypass.
4. `ICalibreMetadataReader` opens the database read-only, validates its schema, and maps database-only records. SQLite and provider types remain internal to Infrastructure.
5. The use case asks `ILibraryPathResolver` to derive each expected format path and determine whether that exact file exists. A rejected path or missing file becomes a `LibraryFinding`; other books/formats continue.
6. The use case constructs a deterministically ordered immutable `LibrarySnapshot` and returns `LibraryScanOutcome.Success`.
7. The ViewModel replaces its displayed rows only after a successful scan. A failed or canceled replacement scan does not display a partial snapshot as if it were complete.

Expected validation/read/schema failures return `LibraryScanOutcome.Failure`. Cancellation is propagated as `OperationCanceledException`, caught only at the WPF command boundary, and rendered as a neutral canceled state rather than an error. Unexpected exceptions are logged at the Infrastructure/WPF boundary without book content and converted into a generic actionable error; raw provider exceptions are not shown directly.

### Database schema assumptions that must be verified

The implementation must verify these assumptions against the schema shipped by the supported stable Calibre release before finalizing queries and fixtures. The current official Calibre sources provide the starting contract:

- [`resources/metadata_sqlite.sql`](https://github.com/kovidgoyal/calibre/blob/master/resources/metadata_sqlite.sql) defines the base tables/columns.
- [`src/calibre/db/schema_upgrades.py`](https://github.com/kovidgoyal/calibre/blob/master/src/calibre/db/schema_upgrades.py) defines schema versions/upgrades.
- [`src/calibre/db/tables.py`](https://github.com/kovidgoyal/calibre/blob/master/src/calibre/db/tables.py) shows author-link ordering and format row mapping.
- [`src/calibre/db/backend.py`](https://github.com/kovidgoyal/calibre/blob/master/src/calibre/db/backend.py) shows Calibre's expected format path as `library root + books.path + data.name + "." + lower(data.format)`.

Initial assumptions to confirm and encode in compatibility tests:

| Purpose | Required schema | Mapping assumption |
| --- | --- | --- |
| Schema identity | `PRAGMA user_version` | Record the version and accept only explicitly tested version(s); never run Calibre schema upgrades. Calibre 9.11.0 uses schema version 27. Its final `upgrade_version_26()` method upgrades version 26 and then sets `user_version` to 27. Milestone 1 accepts version 27 only and fails closed for other versions. |
| Library identity | `library_id(uuid)` | Exactly one non-blank UUID identifies the library. Zero/multiple/malformed rows are controlled unsupported/corrupt-library errors. |
| Books | `books(id, title, author_sort, path)` | `id` is an integer primary key; `title` and `path` are non-null; `author_sort` may be null/blank in damaged or older data and is preserved as an empty value plus a data finding rather than synthesized. |
| Authors | `authors(id, name, sort)` | `sort` is the author-specific sort value. Do not reproduce Calibre's locale/tweak-dependent author-sort algorithm. |
| Book/author relation | `books_authors_link(id, book, author)` | For a book, link-row `id` order is Calibre's author order (`ORDER BY books_authors_link.id`), matching Calibre's current `ManyToManyTable` reader. Broken foreign-key references become controlled data findings if the owning book is known. |
| Identifiers | `identifiers(book, type, val)` | Calibre enforces one row per `(book, type)`. Preserve type/value text; compare/order types deterministically without normalizing identifier semantics in this milestone. |
| Formats | `data(book, format, name)` | `format` is the extension token and is unique per book case-insensitively; `name` is the basename without extension. Do not trust either field as a path component until validated. `uncompressed_size` exists upstream but is not required or exposed in Milestone 1. |
| Managed directory | `books.path` | It is a library-root-relative book directory. It may contain Calibre's `/` separators on Windows; both `/` and `\` are parsed as separators, but rooted/traversing/empty components are rejected rather than repaired. |

`CalibreSchemaInspector` will query `sqlite_master` and `PRAGMA table_info(...)` using fixed table names. It will verify all required tables and columns before data queries and return one `UnsupportedSchema` error listing missing elements and the observed `user_version`. It will not inspect, create, migrate, repair, or write schema objects. Queries must use only built-in SQLite behavior and must not rely on Calibre-specific collations, functions, triggers, or views.

The compatibility decision is fail-closed: an untested schema version, non-SQLite file, malformed database, required shape mismatch, or library identity failure stops the scan with an actionable error. Adding another supported schema version later requires an official-source review and a matching synthetic compatibility fixture/test. The implementation target is Calibre 9.11.0/schema 27, verified against the tagged `v9.11.0` versions of `metadata_sqlite.sql`, `schema_upgrades.py`, `tables.py`, and `backend.py`. The original plan incorrectly equated the highest upgrade method suffix (26) with the resulting schema version; Calibre increments `user_version` to 27 after that method runs.

### Proposed Domain types

All types are immutable records or enums and expose read-only collection interfaces. Constructors/factories copy incoming collections so callers cannot mutate a completed snapshot.

- `CalibreBookId(long Value)` and `CalibreAuthorId(long Value)` prevent ID mix-ups and require positive values.
- `LibraryIdentity(string CalibreLibraryUuid, int SchemaVersion, string LibraryRoot)` records the scanned library without using filesystem-specific types.
- `BookAuthor(CalibreAuthorId Id, string Name, string SortName)` carries both author text and Calibre's stored per-author sort value.
- `BookIdentifier(string Type, string Value)` retains Calibre identifier data without implementing identifier normalization.
- `FormatFileStatus` has `Present`, `Missing`, and `InvalidPath` values only.
- `BookFormat(string Format, string StoredFileName, string ExpectedRelativePath, FormatFileStatus FileStatus)` represents a declared format and its exact expected relative path; it contains no hash, assessment, or open stream.
- `CalibreBook(CalibreBookId Id, string Title, string AuthorSort, IReadOnlyList<BookAuthor> Authors, IReadOnlyList<BookIdentifier> Identifiers, IReadOnlyList<BookFormat> Formats, string RelativeDirectory)` is the Milestone 1 projection.
- `FindingSeverity` initially has `Information`, `Warning`, and `Error`; missing files and invalid managed paths are warnings because they do not invalidate other snapshot data.
- `LibraryFinding(string Code, FindingSeverity Severity, string Message, string SuggestedAction, CalibreBookId? BookId, string? Format, string? RelativePath, IReadOnlyDictionary<string, string> Evidence)` is deterministic and actionable. Evidence must contain metadata/path facts only, never book content.
- `LibrarySnapshot(LibraryIdentity Identity, DateTimeOffset ScannedAt, IReadOnlyList<CalibreBook> Books, IReadOnlyList<LibraryFinding> Findings)` follows the documented principal model.

Domain validation covers IDs, blank format/type values, and immutable collection ownership. Path validity, SQLite records, logging, cancellation, and UI state do not enter Domain.

### Application interfaces, contracts, and use cases

Application owns all integration ports and structured operation contracts:

- `IClock.GetUtcNow()` supplies `ScannedAt` deterministically.
- `ILibraryPathResolver.ValidateAsync(string candidatePath, CancellationToken)` validates/canonicalizes the root and top-level database and returns `LibraryValidationOutcome`.
- `ILibraryPathResolver.ResolveFormat(ValidatedLibraryLocation library, string relativeDirectory, string storedName, string format)` returns `ResolvedFormatPath` or a path-specific failure; the application never calls `System.IO` directly.
- `ILibraryPathResolver.FileExistsAsync(ResolvedFormatPath path, CancellationToken)` checks only the expected file.
- `ICalibreMetadataReader.ReadAsync(ValidatedLibraryLocation library, IProgress<LibraryScanProgress>? progress, CancellationToken)` returns a provider-neutral `CalibreCatalogReadOutcome` containing `CalibreCatalogRecord` data or a `LibraryError`.
- `ValidateLibraryUseCase.ExecuteAsync(...)` provides immediate selection validation for the UI.
- `ScanLibraryUseCase.ExecuteAsync(...)` revalidates, reads the catalog, resolves/checks formats, creates findings, and returns a `LibraryScanOutcome`.

Application-only records include `ValidatedLibraryLocation`, `CalibreCatalogRecord`, `CalibreBookRecord`, `CalibreAuthorRecord`, `CalibreIdentifierRecord`, `CalibreFormatRecord`, `ResolvedFormatPath`, `LibraryValidationOutcome`, `CalibreCatalogReadOutcome`, `LibraryScanOutcome`, `LibraryError`, `LibraryErrorCode`, `LibraryScanProgress`, and `LibraryScanPhase`. They use strings, records, domain values, `IProgress<T>`, and `CancellationToken`; they expose no SQLite, filesystem object, WPF, logging-provider, or process types.

`LibraryErrorCode` must at least distinguish `EmptyPath`, `FolderNotFound`, `FolderNotReadable`, `MetadataDatabaseNotFound`, `MetadataDatabaseNotAFile`, `MetadataDatabaseNotReadable`, `NotSqliteDatabase`, `UnsupportedSchema`, `CorruptDatabase`, `DatabaseBusy`, and `UnexpectedReadFailure`. Every `LibraryError` contains a safe user message and a concrete suggested action. Technical exception detail is logged, not returned as UI text.

Domain finding codes initially include:

- `FORMAT_FILE_MISSING`: the exact expected path does not exist; advise checking Calibre's library maintenance tools or restoring the file outside this application.
- `MANAGED_PATH_INVALID`: `books.path`, `data.name`, or `data.format` cannot safely produce a contained path; advise repairing the library with Calibre. No existence check is attempted.
- `AUTHOR_REFERENCE_MISSING`: a link points to a missing author row; preserve the book and advise Calibre library maintenance.
- `CATALOG_VALUE_INVALID`: a non-structural row value cannot be represented faithfully; preserve other valid records and identify the field.

Structural uncertainty is not downgraded to findings: a missing required table/column, unreadable query, duplicate format key that makes a book ambiguous, or invalid library identity fails the scan so the UI never presents an unreliable snapshot as complete.

### Infrastructure implementation

- Add centrally versioned `Microsoft.Data.Sqlite` to Infrastructure. No SQLite package is referenced by Domain or Application.
- `LibraryPathResolver` uses `Path.GetFullPath`, explicit file/directory checks, and platform-aware containment. It never creates directories or files.
- `ReadOnlySqliteConnectionFactory` builds a connection with `SqliteOpenMode.ReadOnly`, private cache, and pooling disabled. Immediately after open it executes `PRAGMA query_only = ON` and confirms `PRAGMA query_only` returns `1`. It never sets journal mode, performs migrations, attaches databases, or uses a writable/temp database under the library root.
- Do not use SQLite `immutable=1`: it can ignore concurrent changes and is unsafe for a live library. If a read-only WAL database cannot be opened without creating sidecars, fail with an actionable busy/read error rather than relaxing read-only/no-create guarantees.
- `CalibreSchemaInspector` validates version and shape before any catalog query.
- `SqliteCalibreMetadataReader` issues fixed parameterless `SELECT` statements with explicit column lists. It reads books ordered by ID, author links ordered by link ID, identifiers ordered by book/type/value, and formats ordered by book/format/name. It maps rows into Application records and detects broken/ambiguous relationships.
- SQLite work runs off the WPF dispatcher because `Microsoft.Data.Sqlite` cannot provide truly asynchronous SQLite I/O. The Infrastructure implementation owns the background boundary, checks cancellation before/after open, between queries, and on every row, and disposes reader/command/connection in `finally`/`await using` paths.
- `ConfigureAwait(false)` is used below the WPF layer. No synchronous wait (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`) is introduced.
- `Microsoft.Extensions.Logging` records operation phase, safe library identity/path metadata, schema version, row counts, duration, and error code. It does not log titles, author names, identifiers, format contents, or raw SQL/provider exception text at user-facing levels.
- `ServiceCollectionExtensions.AddInfrastructure()` registers the resolver, clock, connection factory, schema inspector, and metadata reader. The WPF composition root registers Application use cases and WPF services/ViewModels.

### Path-resolution rules

1. Reject null, empty, or whitespace selection before any filesystem call.
2. Canonicalize the selected root once with `Path.GetFullPath`; require an existing readable directory.
3. Resolve exactly `Path.Combine(root, "metadata.db")`; require an existing regular file. Do not search parents, children, alternate names, or Calibre configuration.
4. Treat `books.path` as a relative directory. Reject rooted paths, device/UNC paths, `.` or `..` segments, empty internal segments, invalid characters, and any canonical result outside the canonical library root.
5. Require `data.name` to be exactly one non-blank filename stem: no directory separators, root, `.`/`..`, trailing separator, or invalid filename characters.
6. Require `data.format` to be a non-blank extension token containing only the verified Calibre-safe extension character set. Canonicalize the display/token to uppercase; derive the on-disk extension with `ToLowerInvariant()`.
7. Build the expected filename as `${data.name}.${data.format.ToLowerInvariant()}` and the expected relative path as `books.path + expected filename`. Preserve the original managed directory separately for display/evidence.
8. Canonicalize the combined full path and perform a separator-boundary-aware containment check against the root. The root itself is not a valid format path.
9. Check only this exact expected file with a non-mutating existence/type query. A directory at that path is treated as missing/anomalous, not present.
10. Do not enumerate the book directory, perform case repair, follow an alternate same-extension filename, rename anything, or call Calibre's `format_abspath` fallback behavior.
11. Symlink/reparse-point handling must be verified on Windows during implementation. The safe default is to reject a format path whose existing ancestor below the selected root is a reparse point unless its fully resolved target can be proven to remain under the fully resolved root. This is a finding, not permission to inspect outside the library.

The snapshot stores normalized library-relative paths, not absolute format paths. Absolute paths remain Application/Infrastructure values and are used only for existence checks and safe error context.

### Cancellation and progress design

`LibraryScanProgress` contains `LibraryScanPhase`, `CompletedUnits`, `TotalUnits`, and a short safe status message. Phases are `Validating`, `OpeningDatabase`, `ValidatingSchema`, `ReadingBooks`, `ReadingAuthors`, `ReadingIdentifiers`, `ReadingFormats`, `ResolvingFiles`, and `Completed`.

- The metadata reader obtains read-only row counts for each relevant dataset, allowing determinate progress while iterating rows. Opening/schema validation may be indeterminate.
- The application reports one resolving unit per declared format. Empty libraries still transition through all phases and complete normally.
- Progress totals are monotonic within a phase and callbacks are rate-limited/coalesced if necessary so tens of thousands of rows do not flood the WPF dispatcher.
- The WPF layer creates `Progress<LibraryScanProgress>` on the dispatcher and binds phase text plus a determinate/indeterminate `ProgressBar`.
- `MainWindowViewModel` creates one `CancellationTokenSource` per scan. `CancelCommand` only requests cancellation and disables itself; it never blocks waiting for completion.
- A second scan cannot start while one is active. Selecting another folder is disabled while scanning to keep displayed input and operation identity consistent.
- Cancellation is checked at every boundary/row/path check. All database objects are disposed, no partial snapshot is committed to the ViewModel, the status becomes `Scan canceled`, and Scan can be run again.
- Window shutdown requests cancellation and asynchronously allows disposal through the hosted application lifetime; no sync-over-async is used.

### WPF ViewModels and minimal views

- `MainWindowViewModel` exposes selected library path, validation/status/error/action text, busy/canceled state, progress values, a read-only books collection, selected book, and commands for Browse, Scan, and Cancel.
- `BookRowViewModel` formats authors/author sort for display and exposes the source `CalibreBook` without changing it.
- `FormatRowViewModel` exposes format, stored filename, expected relative path, and a text status (`Present`, `Missing`, or `Invalid path`). Status must not rely on color alone.
- Successful scans atomically replace the row collections and select the first book when present. Empty libraries show a clear empty-state message.
- `MainWindow.xaml` contains: a labeled read-only selected-folder field; Browse, Scan, and Cancel buttons; accessible progress/status text; an actionable error panel; a books `DataGrid`; and a selected-book formats `DataGrid`. Missing formats remain visible and findings/counts are summarized.
- Commands and controls receive labels/access keys, keyboard focus order, automation names where needed, text wrapping, and resize-friendly layout. No cleanup/recommendation controls appear.
- `ILibraryFolderPicker` and `OpenFolderDialogLibraryFolderPicker` are WPF-only presentation services. The ViewModel does not access the filesystem or SQLite.
- `MainWindow.xaml.cs` remains limited to `InitializeComponent`. `App.xaml.cs` becomes the composition root/host lifecycle; Infrastructure is referenced nowhere else in WPF code.

### Error and finding model

Errors and findings have different semantics:

- A `LibraryError` means no trustworthy complete snapshot is available. It is returned for selection/permission/database/schema/query failures and displayed in a top-level error panel with a suggested action. The previous successful results, if any, remain visually identified as previous rather than silently replaced.
- A `LibraryFinding` means the scan completed and a specific catalog/file inconsistency needs attention. Findings are part of the immutable snapshot and do not stop unrelated rows. Missing files are the principal Milestone 1 finding.
- Cancellation is neither an error nor a finding.
- Unexpected faults receive a correlation/event ID in logs and a generic UI action such as retrying after closing Calibre or inspecting the application log. Provider stack traces and book metadata are not shown.

Examples of actionable messages:

- `Metadata database not found` / `Choose the top-level Calibre library folder that directly contains metadata.db.`
- `The library schema is not supported (schema 26; expected 27)` / `Open and update the library with a supported Calibre version, or update Calibre Library Cleaner.`
- `The database is busy or changing` / `Wait for Calibre library maintenance to finish, close other tools using the library, and retry.`
- `Expected EPUB file is missing` / `Use Calibre's Library maintenance tools or restore the file; this scan made no changes.`

## Files expected to change

The implementation should use the following concrete file set. Deviations must be recorded in this plan before code is changed.

### Repository/build and documentation

- `CalibreLibraryCleaner.sln` — add the focused WPF test project.
- `Directory.Packages.props` — add the centrally managed `Microsoft.Data.Sqlite` version.
- `docs/architecture.md` — add `CalibreLibraryCleaner.Wpf.Tests` to the test-project topology/responsibility description; production dependency direction remains unchanged.
- `docs/plans/milestone-1-read-only-library-snapshot.md` — keep progress, deviations, verification results, and final outcome current.

### Domain

- `src/CalibreLibraryCleaner.Domain/Libraries/CalibreBookId.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/CalibreAuthorId.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/LibraryIdentity.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/BookAuthor.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/BookIdentifier.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/FormatFileStatus.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/BookFormat.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/CalibreBook.cs`
- `src/CalibreLibraryCleaner.Domain/Findings/FindingSeverity.cs`
- `src/CalibreLibraryCleaner.Domain/Findings/LibraryFinding.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/LibrarySnapshot.cs`

`AssemblyMarker.cs` remains.

### Application

- `src/CalibreLibraryCleaner.Application/Abstractions/IClock.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ILibraryPathResolver.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ICalibreMetadataReader.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ValidatedLibraryLocation.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ResolvedFormatPath.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/CalibreCatalogRecord.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryErrorCode.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryError.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryValidationOutcome.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/CalibreCatalogReadOutcome.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanOutcome.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanPhase.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanProgress.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ValidateLibraryUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs`

`CalibreCatalogRecord.cs` contains the internal provider-neutral book/author/identifier/format row records to avoid a file per trivial transport record. `AssemblyMarker.cs` remains.

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/CalibreLibraryCleaner.Infrastructure.csproj` — reference `Microsoft.Data.Sqlite`.
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Time/SystemClock.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Paths/LibraryPathResolver.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Sqlite/ReadOnlySqliteConnectionFactory.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Sqlite/CalibreSchemaContract.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Sqlite/CalibreSchemaInspector.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Sqlite/SqliteCalibreMetadataReader.cs`

`AssemblyMarker.cs` remains. No writable repository, migration, hashing service, ebook reader, or Calibre process wrapper is added.

### WPF

- `src/CalibreLibraryCleaner.Wpf/App.xaml` — remove `StartupUri` and retain application resources.
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs` — build/start/stop the host and resolve `MainWindow`.
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml` — implement the minimal accessible master/detail snapshot view.
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml.cs` — accept the ViewModel through construction and keep only view initialization.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/BookRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/FormatRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/ILibraryFolderPicker.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/OpenFolderDialogLibraryFolderPicker.cs`

The existing WPF project file is not expected to need another package because folder dialog, hosting, and CommunityToolkit.Mvvm dependencies are already available.

### Tests and fixtures

- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibrarySnapshotTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibraryValueTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ValidateLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ScanLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj` — add direct SQLite fixture dependency if required by compilation.
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/TemporaryDirectory.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticCalibreLibrary.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/LibraryStateCapture.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Paths/LibraryPathResolverTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Sqlite/CalibreSchemaInspectorTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Sqlite/SqliteCalibreMetadataReaderTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Safety/ReadOnlyLibraryScanSafetyTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs` — extend prohibited dependency/type rules and assert Infrastructure use in WPF is composition-root-only.
- `tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`

Existing smoke tests remain unless superseded only by equivalent behavior tests; avoid unrelated cleanup.

## Safety considerations

- The production SQLite connection is created only with `Mode=ReadOnly`; `PRAGMA query_only=ON` is defense in depth, not a substitute for read-only open mode.
- The SQL surface is a closed set of fixed `SELECT`/read-only `PRAGMA` statements. No generic execute method or SQL string is exposed outside the private Infrastructure reader.
- The application does not invoke Calibre code, schema upgrades, CLI commands, database backup APIs, or mutable filesystem APIs.
- Path resolution uses only canonicalization and metadata/existence checks. It cannot create a missing folder/file or repair a path.
- Missing, malformed, or unsafe format paths are reported; they never trigger directory enumeration, alternative-file adoption, or renames.
- The database and all entries beneath the temporary test library are captured before and after successful, failed, and canceled scans. Tests compare names, file bytes/hashes, sizes, attributes, and last-write timestamps and assert no SQLite journal/WAL/SHM or other file was created by the scan.
- A cancellation registration must never dispose shared UI state or leave a pooled connection alive; pooling is disabled and every owned resource is disposed.
- Scanning a library while Calibre is changing it can produce a busy/consistency failure. The application fails closed and asks the user to retry; it does not copy or repair database state in the library.
- Synthetic fixtures are always created beneath test-owned temporary directories. Safety tests may delete only those test-owned directories during cleanup.
- Logs exclude book content and identifiers by default. User-facing errors are sanitized and do not disclose stack traces.
- No AI component is present, so no recommendation or finding can trigger an action.

## Implementation steps

1. Recheck the selected stable Calibre release's official schema/path code, record the exact supported `user_version` contract in this plan, and capture the minimal required tables/columns in `CalibreSchemaContract`.
2. Add `Microsoft.Data.Sqlite` centrally and to Infrastructure; restore immediately to expose compatibility/analyzer issues early.
3. Add the Milestone 1 immutable Domain values and invariant/collection-ownership tests. Do not add series/language/hash/duplicate types.
4. Add Application result/progress/catalog contracts and the three ports.
5. Implement `ValidateLibraryUseCase` and `ScanLibraryUseCase` with deterministic ordering, validation recheck, non-fatal missing-file/path findings, cancellation propagation, and injected time.
6. Build synthetic SQLite/filesystem fixture builders for empty, valid, multiple-author/multiple-identifier/multiple-format, missing-file, invalid-path, missing-schema, malformed-database, broken-reference, cancellation, and safety scenarios.
7. Implement and test `LibraryPathResolver`, including containment, mixed separators, invalid components, filename/format validation, case behavior, directory-at-file-path, and reparse-point policy.
8. Implement `ReadOnlySqliteConnectionFactory` and `CalibreSchemaInspector`; add integration tests for supported shape/version and each controlled schema/database failure.
9. Implement `SqliteCalibreMetadataReader`, provider exception mapping, logging, deterministic query order, progress, cancellation checks, and guaranteed disposal.
10. Add full successful/failed/canceled safety tests that snapshot the entire synthetic library before and after scanning and prove no files, bytes, names, attributes, or timestamps changed and no sidecars were created.
11. Add Infrastructure DI registration and wire Application use cases plus WPF services/ViewModels in `App.xaml.cs` only.
12. Implement the folder picker, `MainWindowViewModel`, row ViewModels, and minimal master/detail XAML. Keep code-behind view-only.
13. Add the WPF test project and focused ViewModel tests for picker cancellation, validation failure, progress, successful/empty display, missing-format display, scan cancellation, command enablement, and rescan behavior; update the documented test topology.
14. Extend architecture tests for the new package/type boundaries and WPF composition-root constraint.
15. Run focused tests while iterating, then the full required restore/build/test/format sequence.
16. Launch the WPF app against a test-owned synthetic library for a manual accessibility/responsiveness smoke test. Do not point it at a real user library for verification.
17. Review the complete diff and package graph for future-scope leakage, writable APIs, unsafe SQL/path behavior, content logging, unexpected generated files, and unrelated changes.
18. Update this plan's progress, deviations/failed approaches, final command results, risks, unresolved decisions, and final outcome.

## Tests

### Domain unit tests

- Strong IDs reject non-positive values and remain distinct types.
- Book/format/identifier/author required values reject invalid input without filesystem or SQLite concepts.
- Snapshot/book/finding constructors defensively copy collections and expose no mutable collection.
- A missing/invalid format can coexist with present formats in the same immutable book/snapshot.
- Findings retain deterministic evidence and actionable text.

### Application unit tests

- Empty/invalid selection returns the exact validation error without calling the metadata reader.
- Scan revalidates even after an earlier successful validation.
- A valid catalog maps all requested fields, preserves author-link order and stored author sorts, and orders books/identifiers/formats/findings deterministically.
- Empty valid library succeeds with zero books.
- Each present expected format is marked `Present`.
- A missing format becomes one `FORMAT_FILE_MISSING` finding while the scan and other formats succeed.
- An invalid/traversing format path becomes `MANAGED_PATH_INVALID` and is never passed to the existence check.
- Broken author references/non-structural invalid row values produce defined findings; ambiguous duplicate format records fail closed.
- Reader/validation failures are preserved as actionable `LibraryError` values.
- Cancellation during validation, reading, and path resolution propagates and stops subsequent calls.
- Progress phases/order/totals are monotonic and complete exactly once.
- Injected clock supplies `ScannedAt`; tests never depend on wall-clock time.

Use FakeItEasy for all ports/clock and FluentAssertions for outcomes and call assertions.

### Infrastructure integration tests

- Open and read an empty supported synthetic SQLite library.
- Load multiple books with multiple ordered authors and individual/book author-sort values, identifiers, formats, stored basenames, relative paths, and library UUID/schema version.
- Detect non-SQLite, malformed, locked/busy, missing-table, missing-column, and unsupported-version databases as the correct controlled errors.
- Verify read-only/query-only connection behavior and confirm a write statement cannot succeed through the configured connection policy without exposing a writable production API.
- Respect cancellation before open, between datasets, during row iteration, and during file resolution; release handles afterward.
- Resolve normal and mixed-separator Calibre paths to exact expected filenames.
- Reject rooted paths, traversal, prefix-collision paths (for example `Library2` beside `Library`), invalid names/extensions, directory-at-file-path, and unsafe reparse targets.
- Treat missing files as absence without creating them or enumerating/renaming alternatives.
- Map SQLite/provider exceptions to safe codes/messages and log structured metadata without titles, authors, identifiers, or content.

Fixtures build only the minimal verified Calibre schema with fixed data and can deliberately omit/corrupt elements per test. They are generated at runtime under unique temporary roots; no `.db` copied from a user library is checked in.

### WPF/ViewModel tests

- Folder-picker cancellation leaves selection unchanged; selection updates validation state when a folder is chosen.
- Scan/Cancel/Browse command enablement tracks busy state and prevents concurrent scans.
- Progress and status properties reflect all phases without dispatcher blocking assumptions in the ViewModel.
- Validation and scan errors expose message plus suggested action and permit retry.
- Success displays all books, selects the first book, and displays its formats/statuses; an empty snapshot displays an empty state.
- Missing formats remain visible as `Missing` and update the findings summary.
- Cancellation invokes the token, shows a neutral canceled state, discards partial results, and allows a later scan.
- A failed/canceled rescan does not masquerade stale rows as the new library's results.

Manual XAML smoke testing covers labels/access keys, keyboard tab order, resize/high-DPI behavior, progress visibility, readable missing status without color, and window close during scan. No automated test uses a real library.

### Architecture tests

- Domain references no solution project or SQLite/filesystem/WPF/logging/DI assembly/package.
- Application references only Domain and no SQLite, concrete filesystem, WPF, logging/DI, or process implementation package.
- Infrastructure references Application and not WPF; SQLite is confined to Infrastructure.
- WPF references Application and Infrastructure, but production Infrastructure namespaces/types appear only in `App.xaml.cs` (composition root).
- WPF ViewModels contain no `Microsoft.Data.Sqlite`, `System.IO`, dialog implementation, or Infrastructure dependencies.
- Domain public APIs contain no SQLite, filesystem handle/path object, WPF, process, or provider types.
- Existing project-reference assertions continue to pass; the test-only WPF project does not alter production direction.

### Safety tests

- For success, validation failure after access begins, database read failure, and cancellation, compare a recursive before/after manifest of every synthetic library entry: relative name, kind, bytes/hash, length, attributes, and creation/last-write timestamps.
- Hash `metadata.db` before/after and assert exact equality.
- Start without `metadata.db-journal`, `metadata.db-wal`, or `metadata.db-shm` and assert none is created by the scanner.
- Assert no file/directory appears, disappears, moves, or changes inside the library.
- Verify handles/connections are disposed by renaming and then deleting only the test-owned fixture after the scan.
- Verify an alternate same-extension file is neither selected nor renamed when the exact expected filename is missing.
- Search production code in review/tests for SQL mutation verbs and mutable filesystem calls; any necessary mutable APIs must exist only in the synthetic fixture builder/test cleanup.

## Verification commands

Run focused tests during implementation, followed by the standard clean sequence:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Useful focused commands before the full suite:

```powershell
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj
```

Also run a manual WPF smoke test against a generated/test-owned fixture and review:

```powershell
git status --short
git diff --check
git diff --stat
git diff
```

Do not report any command as successful until it actually completes successfully. The final full test count and any warnings/failures belong in `Progress` and `Final outcome`.

## Risks

- Calibre's schema is an implementation detail and can change. Version/shape checks and official-source compatibility fixtures reduce, but do not eliminate, this risk.
- A live SQLite database in WAL mode may require existing sidecar state for a read-only connection. The no-create rule can make some actively changing libraries unreadable; fail-closed guidance must be clear.
- SQLite work is not truly asynchronous in `Microsoft.Data.Sqlite`; a background boundary plus frequent cancellation checks is needed to avoid blocking WPF, and cancellation cannot interrupt every native call instantly.
- Author ordering depends on `books_authors_link.id`, an assumption derived from Calibre's current reader rather than a declarative foreign-key/order column.
- Case sensitivity, Windows reserved names, long paths, junctions/symlinks, UNC roots, and mixed separators can make containment/existence behavior platform-specific.
- Checking database rows and files is not a transaction across SQLite and the filesystem. Calibre could change files during a scan, yielding a controlled missing finding or scan failure. This milestone does not lock or copy the library.
- Capturing timestamps in safety tests can be filesystem-resolution-sensitive. Fixtures must set stable timestamps and compare with the filesystem's supported precision without weakening byte/name/no-create assertions.
- A new WPF test project expands the documented test topology but keeps production dependency direction intact; architecture documentation must be updated in the same implementation.
- Detailed ViewModel progress updates can overwhelm the dispatcher on very large libraries unless coalesced.
- Holding a complete immutable snapshot in memory is acceptable for Milestone 1 but must be profiled with large synthetic catalogs before later analysis adds hashes/assessments.

## Unresolved questions

- Which exact stable Calibre release(s) and `PRAGMA user_version` values define initial support? Proposed default: pin the implementation/tests to the current stable release's schema after verification and fail closed for all untested versions.
- Does `Microsoft.Data.Sqlite` read-only mode on every supported Windows/Calibre WAL state satisfy the no-sidecar-creation invariant? This must be proven by integration tests; if not, the product decision is to reject that state with guidance, not copy into or write within the library.
- Should a read-only scan be permitted while the Calibre GUI has the library open? Proposed default: permit it when SQLite can provide a consistent read-only view; surface busy/changing errors and never bypass locks with `immutable=1`.
- What is the supported policy for library roots or book directories containing junctions/symlinks? Proposed fail-closed policy is documented above, but it needs Windows fixture verification and may need an ADR if linked libraries are common.
- Should an individual blank `authors.sort` be a non-fatal `CATALOG_VALUE_INVALID` finding or a structural scan failure? Proposed default: finding, preserve the stored blank, and never synthesize locale-dependent values.
- Should the UI retain a previous successful snapshot after a later scan fails? Proposed default: retain it but label it explicitly as previous results for the prior library, preventing accidental attribution to the new selection.

These questions do not authorize broader behavior. Any answer that changes safety, supported schema policy, or project topology must be recorded here before implementation proceeds.

Reply to unresolved questions: the proposed answers are accepted.

## Progress

- [x] Read repository and nested `AGENTS.md` instructions.
- [x] Read `PLANS.md`, all requested product/architecture/domain/test/roadmap documents, all accepted ADRs, and the completed Milestone 0 plan.
- [x] Inspect the current solution, central build/package files, all production source/XAML, all tests, git status, and recent history.
- [x] Review current official Calibre schema/path sources to identify assumptions that implementation must pin and verify.
- [x] Create the Milestone 1 execution plan.
- [x] Resolve and record supported Calibre schema version(s) and accepted reparse/WAL policies (Calibre 9.11.0/schema 27; fail closed for unsafe linked paths or WAL states that cannot be opened without creation).
- [x] Implement Domain and Application portions.
- [x] Implement Infrastructure read-only SQLite and path portions.
- [x] Implement the minimal WPF flow.
- [x] Add synthetic fixtures and unit/integration/UI/architecture/safety tests.
- [x] Run restore, build, full tests, and formatting verification.
- [x] Perform a WPF process-startup smoke test and complete diff/package/safety review.
- [ ] Perform an interactive visual/accessibility smoke test with a synthetic fixture (not available in the non-interactive implementation session).

Implementation notes:

- The first implementation restore failed because `Microsoft.Data.Sqlite` 10.0.9 transitively selected `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which NuGet reports under high-severity advisory `GHSA-2m69-gcr7-jv3q`; warnings are errors in this repository. The advisory was not suppressed. The implementation directly pins the current stable `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 bundle so NuGet resolves its patched native SQLite package instead.
- The first compile after adding Infrastructure failed on analyzer rules requiring static helpers, high-performance logging delegates, and concrete private collection parameter types. The code was corrected without suppressing analyzers; the next production build succeeded with zero warnings.
- The first `dotnet format --verify-no-changes` failed only on LF line endings in newly added C# files while `.editorconfig` requires CRLF. `dotnet format` normalized the sources; the final format verification succeeded. No formatting rule was relaxed.
- The plan named a separate `CalibreSchemaInspectorTests.cs`, but schema-version/shape behavior is covered through the public `ICalibreMetadataReader` boundary in `SqliteCalibreMetadataReaderTests.cs` to avoid tests of an internal helper. The plan's proposed `AddInfrastructure()` extension is named `AddCalibreLibraryInfrastructure()` to avoid an overly generic composition API.
- The WPF executable was started hidden after the final build, remained alive for two seconds, and was intentionally stopped. Interactive visual/high-DPI/accessibility inspection and selecting a synthetic folder through the native dialog could not be performed in the non-interactive session; ViewModel behavior is covered by focused automated tests.
- On 2026-07-14, a real startup run exposed WPF's default `TwoWay` behavior for `TextBox.Text` and `ProgressBar.Value`, which cannot target the ViewModel's read-only-to-the-view properties. Both bindings now specify `Mode=OneWay`. A new STA window test constructs, shows, and closes `MainWindow`, covering XAML binding activation that the original ViewModel-only tests did not exercise. A process startup/graceful-close smoke test then completed with empty standard error.
- On 2026-07-14, scanning a real Calibre 9.11.0 library exposed an off-by-one error in the schema contract. The highest tagged method is `upgrade_version_26()`, but Calibre invokes it for a version-26 database and then sets `PRAGMA user_version` to 27. The production contract, synthetic fixture default, unit/integration/UI records, unsupported-version test, error example, progress record, and final outcome were corrected from 26 to 27. SQLite error code 26 (`SQLITE_NOTADB`) remains unchanged because it is unrelated to Calibre's schema version.

## Final outcome

Completed on 2026-07-14.

- Delivered the Milestone 1 WPF vertical slice: choose and validate a Calibre folder, revalidate before scanning, load a version-27 Calibre catalog read-only, resolve exact expected format paths, retain missing/invalid formats as findings, display book/format master-detail lists, show actionable errors/progress, and cancel without committing partial results.
- Verified Calibre 9.11.0's tagged official schema and path implementation. The reader accepts `PRAGMA user_version = 27` only; requires `library_id`, `books`, `authors`, `books_authors_link`, `identifiers`, and `data` columns documented above; preserves author order by `books_authors_link.id`; and derives `${books.path}/${data.name}.${lower(data.format)}` without Calibre's fallback rename behavior. The original version-26 assumption was corrected after reviewing Calibre's increment-after-upgrade loop.
- SQLite is confined to Infrastructure and opened with `SqliteOpenMode.ReadOnly`, private cache, disabled pooling, and confirmed `PRAGMA query_only = ON`. The production SQL surface contains only fixed `SELECT` statements and read-only/connection-local `PRAGMA` statements.
- Filesystem behavior in production is confined to Infrastructure path validation, canonicalization, attribute inspection, and exact `File.Exists` checks. It contains no create, write, copy, rename, move, overwrite, or delete calls.
- Added generated synthetic SQLite/filesystem fixtures only. Safety tests compare recursive names, kinds, attributes, lengths, creation/last-write timestamps, and SHA-256 test hashes before/after present-file, missing-file, and canceled scans; assert no SQLite sidecars are created; and prove an alternate EPUB is neither adopted nor renamed.
- Final `dotnet restore` succeeded.
- Final `dotnet build --no-restore` succeeded with zero warnings and zero errors.
- Final `dotnet test --no-build` succeeded: 39 passed, 0 failed, 0 skipped across Domain (5), Application (5), Infrastructure (16), Architecture (9), and WPF (4).
- Final `dotnet format --verify-no-changes` succeeded.
- `dotnet list package --vulnerable --include-transitive` reported no vulnerable packages in any project.
- WPF process-startup smoke test succeeded. Interactive visual/accessibility testing remains manual follow-up.
- Complete diff review and production mutation scan found no hashing, duplicate detection, EPUB/PDF inspection, scoring, recommendations, cleanup plans, Calibre CLI integration, SQL mutation statements, or mutable library filesystem operations.
- The schema-27 correction reran the full verification suite on 2026-07-14: all standard commands passed, the dependency audit found no known vulnerable packages, the WPF startup/graceful-close smoke test produced empty standard error, and the production mutation scan remained empty.
- Remaining risks: only Calibre schema 27 is supported; active/atypical WAL states can fail closed; linked/junction library paths are rejected; SQLite native calls are not instantly cancellable; filesystem/database state can change between independent read checks; and visual/high-DPI/accessibility behavior still needs an interactive synthetic-library smoke test.
