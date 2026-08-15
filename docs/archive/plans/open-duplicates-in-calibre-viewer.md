# Open Duplicate Records in Calibre Viewer

## Objective

Allow users to double-click member rows in Exact file duplicates and Metadata candidates to inspect the selected book format in Calibre's `ebook-viewer.exe` before choosing a keeper.

## Scope

- Exact duplicate rows open their represented format file.
- Metadata candidate rows open the first present format using reading preference order: EPUB, AZW3, MOBI, PDF, then other formats alphabetically.
- Launch only the trusted `ebook-viewer.exe` sibling of the configured Calibre executable.
- Validate the selected file is a physical regular file contained in the selected library.
- Launch without a shell and pass the file through `ProcessStartInfo.ArgumentList`.
- Report missing viewer, missing format, unsafe path, and process-start failures in the existing WPF error area.
- Add double-click behavior to both member grids.

## Out of scope

- Choosing a format through a dialog.
- Launching missing or projected-only files.
- Shell file associations.
- Waiting for or controlling the viewer after successful launch.
- Opening cover images or metadata records without formats.

## Relevant requirements

- External viewing is an explicit user action and must not mutate the Calibre library.
- Paths and executables are untrusted local input and require canonical containment/reparse validation.
- Application and Domain must not launch processes or depend on filesystem implementation details.
- Book content and physical paths must not be logged.

## Existing implementation inspected

- No viewer-launching abstraction exists.
- `ExecutionPathGuard.TryValidateContainedRegularFile` validates managed library files.
- `CalibreExecutionOptions.TrustedExecutablePath` identifies `calibredb.exe`; `ebook-viewer.exe` is a trusted sibling.
- Exact member rows expose one relative format path.
- Metadata member rows represent multi-format books and currently retain only display text.
- WPF code-behind is limited to view-only event routing and can forward double-clicks to ViewModel commands.

## Proposed design

Application declares `IEbookViewerLauncher` with a typed launch request/result. Infrastructure validates the trusted viewer and contained file, then starts one process using `UseShellExecute=false`, `CreateNoWindow=false`, and one argument-list item.

Metadata member rows retain the selected launch format/path chosen from present scan-observed formats. MainWindowViewModel exposes commands for the selected exact and metadata member. XAML `MouseDoubleClick` handlers route the view event to those commands; the ViewModel owns outcome/error presentation.

## Files expected to change

- `src/CalibreLibraryCleaner.Application/Abstractions/IEbookViewerLauncher.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreEbookViewerLauncher.cs`
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateMemberRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateGroupRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml.cs`
- Focused infrastructure and WPF tests plus architecture/documentation updates.

## Safety considerations

- Reject absolute/traversal paths and files outside the selected library.
- Reject reparse points in viewer and managed-file paths.
- Do not use `UseShellExecute=true`, command strings, or shell quoting.
- Do not expose physical paths in user-facing technical errors.
- Opening a viewer never changes projected state or cleanup selection.

## Implementation steps

1. Add Application request/result/launcher contract.
2. Implement trusted viewer discovery, file validation, and no-shell process launch.
3. Add infrastructure tests for success, missing viewer, missing file, and unsafe path.
4. Add metadata preferred-format selection and ViewModel open commands.
5. Wire double-click handlers for both grids.
6. Add WPF/architecture tests and documentation.
7. Run complete build/test/format/diff/package verification.

## Tests

- Exact member launch uses its precise relative path.
- Metadata member chooses EPUB before PDF and ignores missing formats.
- No present metadata format returns a controlled error.
- Viewer missing or reparse-pointed is rejected.
- Traversal/missing managed paths are rejected.
- Successful launch receives exactly one physical file argument.
- Double-click routing invokes only the matching command.
- Existing keeper selection behavior remains unchanged.

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

- Metadata records may contain multiple readable formats; deterministic preference may not match every user preference.
- Calibre installations without `ebook-viewer.exe` cannot use this feature.
- The viewer process may outlive the cleaner, which is intentional.

## Unresolved questions

None.

## Progress

- [x] Existing rows, Calibre discovery, path guards, process patterns, and XAML inspected.
- [x] Launcher boundary implemented and tested.
- [x] WPF double-click interaction implemented and tested.
- [x] Documentation aligned.
- [x] Complete verification passed.

## Final outcome

Implementation completed on 2026-08-08. Double-clicking an Exact file duplicate member opens its represented format in trusted Calibre `ebook-viewer.exe`. Double-clicking a Metadata candidate opens the first present format in EPUB, AZW3, MOBI, PDF, then alphabetical preference order. Records without a present format and missing/unsafe viewer or book paths produce controlled WPF errors.

The launcher validates the trusted sibling executable and contained physical book file, uses `UseShellExecute=false`, and passes exactly one `ArgumentList` item. Focused controlled-process, ViewModel, metadata preference, XAML activation, and architecture tests passed. The complete solution built successfully and all 461 tests passed with zero failures or skips. Formatting and diff checks passed, and the direct/transitive package audit reported no known vulnerabilities.
