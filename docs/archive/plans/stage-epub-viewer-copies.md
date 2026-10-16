# Stage EPUB viewer copies

## Objective
Prevent Calibre ebook-viewer from modifying library-managed EPUB bytes.

## Scope
Infrastructure launcher staging/cleanup, bounded options, focused synthetic process
tests, safety/functional documentation and standard verification.

## Out of scope
Changing PDF launching, modifying Calibre settings, direct library repair, bookmark
sync, retaining viewer copies, or weakening Candidate changed-file preflight.

## Relevant requirements
Analysis never writes Calibre-managed files; bounded resources; path containment;
no content/metadata/path logging; responsive external process launch.

## Existing implementation inspected
CalibreEbookViewerLauncher, CalibreExecutionOptions, ExecutionPathGuard, controlled
Calibre executable fixture, current scan baseline and record 450's live file facts.

## Proposed design
Copy a validated EPUB under an opaque per-launch directory in a physical per-user
LocalAppData staging root outside the library. Cap bytes, force a dedicated viewer
instance, and pass only the copy. Delete it when that process exits; bounded stale
pruning handles abnormal application termination. Never log paths or retain names.
PDF continues through the Windows default association using the original validated
path because the reported mutation is EPUB bookmark embedding by Calibre viewer.

## Files expected to change
Infrastructure launcher/options, existing launcher and architecture tests, current
safety/functional docs, and this plan.

## Safety considerations
Reject staging roots equal to, inside, or containing the library. Reject reparse
points and oversized inputs. Create opaque directories/files with CreateNew and
read source with no write sharing. Cleanup failure leaves only app-owned staging,
never modifies the library, and is retried by later stale pruning. The staging root
is per-user LocalAppData and paths/book bytes are not logged.

## Implementation steps
1. Add bounded staging options and safe copy lifecycle.
2. Register process-exit cleanup and stale pruning.
3. Test original immutability, opaque arguments, cleanup, failures and unchanged PDF.
4. Run standard verification and document required Rescan for already changed files.

## Tests
All 14 launcher tests passed: the controlled viewer mutates staged bytes while the
original bytes/timestamp remain exact; dedicated instance arguments; asynchronous
exit cleanup; startup failure cleanup; inside-library and oversize rejection;
bounded stale pruning; unsafe/missing/unsupported inputs; unchanged PDF routing.
Architecture tests require staging and prohibit passing the managed EPUB argument.

## Verification commands
All standard gates passed on 2026-10-16:

- `dotnet restore`
- `dotnet build --no-restore`
- `dotnet test --no-build --maxcpucount:1`: 1,080 passed, zero failed or skipped.
- `dotnet format --verify-no-changes`
- `git diff --check`
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

## Risks
Forced dedicated instances open one viewer window per double-click. Abnormal
termination can leave a temporary copy until later bounded pruning. EPUBs above the
1 GiB default cannot be opened through this feature. Staging transiently contains
book bytes but remains opaque, unlogged, per-user and short-lived. No live post-fix
viewer acceptance was performed against the personal library.

## Unresolved questions
None for the approved temporary-copy design.

## Progress
User approved implementation after relevant launcher/test/docs were staged.
Read-only diagnosis proved record 450 changed from 540,792 to 541,093 bytes directly
after Calibre viewer launch; the registered path still existed and metadata.db hash
was unchanged. Baseline SHA-256/observation differed from the live EPUB. Calibre's
documented viewer options expose no read-only mode.

Bounded staging, dedicated launch and process-exit/stale cleanup were implemented.
Focused launcher tests passed before architecture and standard verification. The
architecture guard now requires `--new-instance`, staging and no managed EPUB
argument. Safety/functional requirements document staging privacy and cleanup.
Production/test diffs, resource bounds, path checks and logging reviewed. PDF behavior,
matching/cache policies and mutation authority were unchanged. Full suite passed.

## Final outcome
Completed. EPUB double-click opens an opaque temporary copy; Calibre viewer bookmark
writes cannot change scanned library bytes. PDF still opens through the default
Windows association. Record 450 was changed before this fix, so the current generation
must be replaced by a fresh Scan before Candidate preparation can succeed.