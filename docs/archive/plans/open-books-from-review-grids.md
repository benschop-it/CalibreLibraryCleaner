# Open books from review grids

## Objective
Open books consistently by double-clicking book rows in every tab.

## Scope
Book and format rows in Library, duplicate members, metadata review, final
authors, and EPUB/PDF assessments. EPUB uses Calibre ebook-viewer; PDF uses the
Windows default PDF application. Prefer present EPUB, then PDF, for book rows.

## Out of scope
Mutation changes, group-summary and finding rows, new viewers or dependencies.

## Relevant requirements
Current user request, read-only analysis, contained regular-file validation,
and presentation/integration dependency boundaries.

## Existing implementation inspected
MainWindow row double-click handlers, MainWindowViewModel opening commands,
book/format rows, CalibreEbookViewerLauncher, existing viewer tests, and process
boundary architecture checks.

## Proposed design
Reuse the launcher port and its path guard. Route PDF through Windows file
association and EPUB through the trusted Calibre installation. Add a shared
parameterized row-opening command, routed from the actual clicked row, without
changing selection, keeper, Apply, or Skip policies.

## Files expected to change
Infrastructure launcher and tests; WPF window, view model and tests; process
boundary architecture tests; functional requirements and this plan.

## Safety considerations
No library writes or direct mutation paths. Validate containment and reject
reparse points before opening. Shell execution is limited to validated PDFs.
Do not log paths or book metadata. Tests use synthetic files and fake launchers.
Mutation and parser workers retain their dedicated direct non-shell boundaries.

## Implementation steps
1. Route formats to the correct viewer and test safety and launch arguments.
2. Wire every book/format grid and test target resolution and row routing.
3. Reconcile documentation, run standard verification, and review the diff.

## Tests
Ten launcher tests cover trusted EPUB invocation, PDF associations without a
Calibre installation, unavailable/unsafe paths, unsupported formats, and default
viewer failures. Nine new view-model cases cover Library/Metadata/Final authors
EPUB preference and PDF fallback, unavailable formats, exact format/assessment
paths, and operation-state gating. Existing duplicate-member command tests now
exercise the shared row resolver. A new STA window test raises the routed
double-click in all ten book/format grids, checks clicked-row identity and
unchanged selection, and rejects headers, empty space, and checkboxes.

## Verification commands
All completed successfully on 2026-10-16:

- `dotnet restore`
- `dotnet build --no-restore`
- `dotnet test --no-build --maxcpucount:1`: 988 passed, zero failed or skipped.
- `dotnet format --verify-no-changes`
- `git diff --check`
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

## Risks
Windows PDF associations may be missing; a controlled error explains how to set
one. Managed paths may become stale after cleanup; the launcher fails safely.
Tests exercise process configuration and routed WPF events, not the user's actual
PDF association or real library.

## Unresolved questions
None.

## Progress
Implementation approved after confirming double-click and viewer routing.
Existing relevant requirements changes staged before editing. The initial WPF
run was blocked by the running application; verification resumed only after the
user closed it. Synthetic fixture constructor failures were repaired locally.
The first full-suite run exposed the old blanket non-shell viewer assertion and
a synthetic viewer fixture cleanup race. The architecture assertion now permits
only the validated PDF association route; the EPUB test waits for its synthetic
process to exit before fixture cleanup. Focused reruns and all 988 tests passed.
Production and test diffs reviewed; no dependencies, cache policies, privacy
logging, or mutation behavior changed.

## Final outcome
Completed. Every book/format grid opens its clicked row on double-click. EPUB
opens in Calibre ebook-viewer and PDF opens in the Windows default PDF viewer.
Book rows prefer a present EPUB then PDF; format and assessment rows open the
specific file. Existing review controls and selection behavior are preserved.