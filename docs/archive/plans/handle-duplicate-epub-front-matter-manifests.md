# Handle duplicate EPUB manifest IDs during final review

## Objective

Prevent a malformed EPUB manifest from aborting Prepare Unresolvable review.

## Scope

Explicitly detect duplicate package manifest IDs at bounded front-matter extraction,
return structured incomplete evidence, and continue assessing unrelated records.

## Out of scope

Repairing ebook bytes, selecting an arbitrary duplicate, changing duplicate matching,
altering quality scores, weakening archive preflight or opening a personal library.

## Relevant requirements

- [Architecture](../../architecture.md): bounded read-only per-format inspection.
- [Safety](../../safety-and-rollback.md): no analysis-time library mutations.
- [ADR 0004](../../adr/0004-select-versone-epub-inspection-stack.md): malformed inputs
  become deterministic findings and extracted-fact changes require versioning.

## Existing implementation inspected

Archive preflight rejects duplicate normalized ZIP paths. It does not reject
duplicate manifest IDs. The front-matter inspector constructs its manifest with
`ToDictionary`, allowing an unhandled duplicate-key ArgumentException to escape.
Other book inspection therefore never reaches publication.

## Proposed design

Construct a unique-ID manifest explicitly. Any duplicate ID, including conflicting
media types or identical duplicate declarations, returns a stable unavailable
problem code without choosing a winner. Log the technical code, never the raw ID.
Version EPUB front-matter extraction independently; preserve the PDF version and
existing archive/parser bounds.

## Files expected to change

The existing EPUB inspector, front-matter extractor version, focused generated EPUB
and complete-review tests, and directly related documentation.

## Safety considerations

Unavailable evidence stays incomplete, not exhausted, qualified or automatically
selected for transfer. No writes, retries, general exception catches or mutation
worker calls are introduced.

## Implementation steps

1. Reproduce duplicate-ID failures using generated EPUBs.
2. Replace exception-driven manifest construction with explicit validation.
3. Verify one malformed record cannot stop the complete retained-record pass.
4. Run focused and standard checks when normal build outputs are available.

## Tests

- Repeated IDs with equal/different targets and conflicting media types.
- Duplicate ZIP paths still rejected by existing preflight.
- Distinct IDs may refer to the same resource without being rejected as duplicate IDs.
- Malformed and valid books in one real-inspector review remain accounted for.
- Input bytes unchanged; no candidates invented from an ambiguous manifest.

## Verification commands

Use existing VSTest/xUnit filtered Infrastructure tests, then available project
suites, normal solution build, formatting and whitespace checks.
Do not terminate the user's running cleaner or bypass its output locks.

## Risks

Rejecting an entire malformed manifest can require explicit review even if a reader
chooses one duplicate. This conservative, visible outcome is preferable to guessing
identity or aborting unrelated books.

## Unresolved questions

None. A duplicate ID is ambiguous evidence, not a reason to modify the EPUB.

## Progress

- [x] Traced the duplicate-key path to front-matter manifest construction.
- [x] Existing pseudonym fix changes preserved; user debug process PID 2472 remains running.
- [x] Reproduced the identical duplicate-key exception with generated repeated IDs.
  Four new assertions failed before the fix: two dictionary crashes and two
  conflicting-media cases that incorrectly returned available evidence.
- [x] Replaced front-matter manifest `ToDictionary` with explicit unique-ID
  validation across every declared media type. No broad exception catch or
  arbitrary first/last winner was introduced. Safe diagnostics retain only the
  stable technical code, not the raw manifest ID or path.
- [x] EPUB front-matter extractor advanced to `2.0.1` to invalidate incompatible
  observations; PDF and general assessment/scoring versions remain unchanged.
- [x] Generated malformed/valid two-book review passed using real inspectors.
  Both records are accounted for; the valid book completes, the malformed book
  stays NeedsReview/unselected/not exhausted, and its duplicate-ID code is visible
  in disposition reasons. Catalog and format hashes remain unchanged.
- [x] Focused checks passed: 16 Infrastructure and 34 Application tests.
  Full available suites passed: Infrastructure 366/366 and Application 301/301
  (667 total, none skipped). Formatting and scoped production-diff review passed.
- [x] Normal WPF outputs were initially locked by the user's running cleaner/debugger
  (PID 2472; previous build identified Visual Studio PID 45264). Requested normal
  debug-session closure; user unavailable. No process was terminated, no output
  directory/configuration bypass was used, and the running app still has old code.
  The user subsequently closed the session on 2026-10-24; the block is resolved.
- [x] Regenerated stale generated WPF files with the standard
  `dotnet build --no-restore --no-incremental --verbosity minimal`; build succeeded
  with zero warnings/errors in normal Debug output paths. No alternate
  configuration/output directory, dependency installation or user-process
  termination was used.
- [x] Complete solution verification passed: 1,216 tests (Domain 443,
  Application 301, Infrastructure 366, Architecture 34, WPF 72), none skipped.
  This includes the companion pseudonym-reintroduction fix and its WPF checks.
  Formatting, whitespace and dependency vulnerability audit passed. Plan archived.

## Final outcome

Implemented and verified in the normal Visual Studio outputs. Duplicate manifest
IDs now produce explicit per-record unavailable evidence instead of aborting the
complete review; other records remain accounted for. No personal library, ebook
or displayed review state was modified. The updated executable is ready for F5;
no package or manual test script is needed, and no commit was created.
