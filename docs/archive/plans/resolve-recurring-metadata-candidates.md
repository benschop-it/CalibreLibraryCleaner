# Resolve recurring metadata candidates

## Objective
Correct false InvalidPath classifications for valid managed format names and
determine why the reported metadata groups remain after cleanup.

## Scope
Filename-stem validation, neighboring synthetic path tests, and read-only
diagnosis of the reported candidates and their cleanup policy.

## Out of scope
Library-specific matching aliases, hiding unresolved groups, changing Skip
semantics, library mutation, recovery, or bypassing failed-run Rescan requirements.

## Relevant requirements
Read-only analysis, exact managed paths, same-work matching, complete physical
facts before cleanup, stop on uncertain mutation, and explicit Rescan afterward.

## Existing implementation inspected
LibraryPathResolver, ExactMetadataDuplicateDetector, UnifiedCandidateCleanupPlanner,
LibraryStateDelta projection, saved checkpoint/reusable analysis/manifest, and
the two reported titles' current read-only catalog rows.

## Proposed design
Permit spaces/dots at the end of a stored filename stem because a validated
alphanumeric format extension follows them. Preserve directory restrictions,
path containment, separator/invalid-character rejection, and reparse checks.
Reject Windows device names even when a space precedes the extension. Keep
unresolved metadata groups visible and trace cleanup exclusions independently.

## Files expected to change
Infrastructure path resolver, existing resolver and read-only scan safety tests,
safety documentation, and this plan.

## Safety considerations
No changes to the real library or persisted analysis state. Current library state
is uncertain after a failed managed-name refresh; no further mutation without
explicit Rescan. Automated tests use synthetic fixtures, never the real library.

## Implementation steps
1. Fix false filename-stem rejection and run focused path regressions.
2. Trace why the remaining Bankroet records are not removed.
3. Run standard verification and report confirmed results and limitations.

## Tests
Real temporary files with space/dot before EPUB/PDF extensions; reserved device
names, traversal, unsafe characters, and trailing space/dot directory rejection.
End-to-end synthetic scans verify both files are Present with fingerprints and
observations, preserve the exact registered path, detect metadata duplicates,
and leave the complete library state unchanged. All 28 focused cases passed.

## Verification commands
All standard gates succeeded on 2026-10-16:

- `dotnet restore`
- `dotnet build --no-restore`
- `dotnet test --no-build --maxcpucount:1`: 1,009 passed, zero failed or skipped.
- `dotnet format --verify-no-changes`
- `git diff --check`
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

## Risks
Unsafe paths must not become executable through relaxed stem validation. Saved
groups are not complete execution history, so do not infer unrecorded Skip choices.
The updated application requires explicit Rescan and Candidate analysis to replace
previously saved invalid-path facts. Actual cleanup was not run during this task.

## Unresolved questions
None. The saved reusable analysis retains the exact Unified group and blocker.

## Progress
User approved the fix and follow-up diagnosis. Relevant documentation changes
staged before editing; resolver and neighboring tests had no prior modifications.
Confirmed the Rousseau EPUBs exist, but their registered stems end in a space.
Read-only catalog inspection left metadata.db SHA-256 unchanged.
The saved reusable analysis proves the Bankroet Unified group also contains
record 913, whose valid EPUB filename has the same trailing-space stem. The
cleanup planner skips the entire group when any member lacks verified physical
facts. The two present Bankroet EPUBs have 31 matching archive entry names and
29 identical entry hashes; only package metadata and Calibre bookmarks differ.
No content text was printed. The same resolver fix addresses both reported
groups without changing grouping or weakening cleanup safety.
The first full build was blocked by the running WPF/debug session. Work stopped
until the user closed it; the resumed build and complete test suite passed.
Production/test diffs and safety documentation reviewed. No dependencies, cache
keys, matching policies, cleanup rules, or privacy logging were changed.

## Final outcome
Completed. Valid registered filename stems ending in spaces/dots now resolve
verbatim, while reserved names and unsafe directory/path inputs remain blocked.
Both reported recurring groups share this root cause. Restart the updated app,
explicitly Rescan, and prepare Candidate analysis before reviewed cleanup.