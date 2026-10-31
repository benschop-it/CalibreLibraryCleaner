# Diagnose managed-name worker failures

## Objective

Make managed-name worker failures actionable without guessing their cause, retrying
mutation or discarding uncertainty after partial execution.

## Scope

Phase-specific, bounded privacy-safe managed-name failure codes, user-visible
record identification and explicit Rescan guidance. Exercise the real fixed worker
against generated fixtures and injected failures.

## Out of scope

Retry/skip/continue after mutation failure, inferred successful chunk prefixes,
direct SQLite/filesystem mutation fallback, changing user library/state, rollback,
cache invalidation and recovery.

## Relevant requirements

- [Safety](../../safety-and-rollback.md): stop ambiguous mutation and require Rescan.
- [Architecture](../../architecture.md): fixed supported-API worker and safe logging.
- [ADR 0021](../../adr/0021-matching-first-performance-oriented-product.md): simple,
  backup-assumed mutation and bounded technical diagnostics.

## Existing implementation inspected

Production technical log: chunk 12, operation 59, record 2067,
`RefreshManagedName`, `calibre_api_failure`; 1,100 of 17,160 operations were committed.
The first 58 worker results in that chunk were not committed to projected state.
The application correctly stopped and marked state uncertain.

The worker maps every non-ValueError to `calibre_api_failure`, losing phase,
exception category and numeric OS error. Explicitly read-only Calibre 9.12.0
pre-state capture succeeds for the failed record; current file reads, timestamp
conversion and exclusive read handles also succeed. Current folder matches pure
Calibre path construction. These facts do not reconstruct the original exception.
No retry, user library writes or test use of personal metadata occurred.

## Proposed design

Track managed-name phases (before read-back, supported update, cache clear,
after read-back, verification). Translate exceptions into fixed category codes and
bounded numeric OS errors only; never include exception message, paths, metadata,
arbitrary class names or traceback. Preserve fixed worker-owned validation codes.
Show the failed record ID and Rescan requirement in the existing blocking issue.
Keep protocol shape and mutation stop/marker semantics unchanged.

## Files expected to change

Fixed Python worker, existing generated worker acceptance, focused generated worker
regressions, Application failure-detail tests and related documentation.

## Safety considerations

Diagnostics do not establish success or authorize continuation. Preserve
`MutationStarted`, fail the entire chunk, do not project its prefix, stop the
session and retain uncertain state. Read-only diagnosis uses supported
`db(..., read_only=True)` or SQLite `mode=ro`/query-only; no live update API runs.

## Implementation steps

1. Reproduce information loss with injected OS/API errors in generated worker tests.
2. Add phase/category-only diagnostics and explicit UI record/Rescan guidance.
3. Exercise successful and failed actual supported-API generated-library mutation.
4. Run focused and standard verification without bypassing a running app's locks.

## Tests

Pre-read, update, cache-clear and post-read failures; numeric error bounds and
message redaction; unchanged worker-owned validation codes; no next operation after
failure; partial-chunk uncertainty/no prefix projection; successful normal worker
read-back and preservation of formats, covers, extra files and metadata.

## Verification commands

Generated Python worker regression suite, real Calibre worker acceptance on a
fresh generated library, focused Application tests, normal Debug build, full
solution tests, format/whitespace and dependency vulnerability checks.

## Risks

The original exception is irretrievably absent from the log. Do not label improved
diagnostics as a proven fix for the user's original cause. An external file lock
or API failure may still require correction outside the app after explicit Rescan.

## Unresolved questions

Original failing phase/category is unknown. Current safe read-only probes do not
reproduce it. Do not rerun mutation on the user's library to obtain this evidence.

## Progress

- [x] Located actual failed operation and confirmed fail-closed state.
- [x] Completed privacy-safe read-only probes; no present read/path/lock failure.
- [x] Generated phase/category tests failed before implementation; all four grouped
  checks now pass, covering the five phases and operation-59 stop boundary.
- [x] Real Calibre 9.12.0 generated fixture passed successful refresh/author writes
  and produced `managed_name_update_path_os_error_errno_13_winerror_32` under an
  intentional Windows sharing lock. No retry; all three records/formats, bytes,
  cover, extra file and local metadata preserved.
- [x] All 62 focused execution tests passed, including 100 committed operations
  before a second-chunk operation-59 failure, no projection of its 58-result prefix,
  explicit record/Scan guidance and refusal of a second mutation.
- [x] Normal Debug build passed with zero warnings/errors; full .NET suite passed:
  Domain 487, Application 326, Infrastructure 384, Architecture 34, WPF 98;
  1,329 total, zero skipped.
- [x] Four grouped generated Python checks and actual Calibre acceptance passed.
  Removed only the test-created bytecode; checks no longer write source bytecode.
- [x] Format verification, whitespace check and dependency vulnerability audit
  succeeded. Preserved all pre-existing staged work without restaging/committing.
- [x] Documented exact outcome and original-cause limitation; archived this
  completed diagnostic improvement, not a claimed production-cause fix.

## Final outcome

The worker's diagnostic blind spot is fixed and its fail-closed behavior verified.
Managed-name exceptions now retain a fixed phase/category and bounded numeric OS
errors, without raw messages, paths, metadata or tracebacks. The user-facing issue
identifies the record and explicitly requires Scan. No retry, skip, inferred prefix,
mutation fallback or cache invalidation was added.

The original production cause remains unknown because its exception was discarded.
A generated Windows sharing violation now produces a verified specific code, but
there is no evidence that the production failure was a sharing violation. Read-only
checks of the reported record succeeded; repeating its mutation solely for
diagnosis would violate the uncertain-state boundary and was not attempted.

User handoff: start the normal application with Visual Studio F5, select the same
library and use **Scan, not Load**. Keep Calibre/viewers/editors closed for later
confirmed mutation and reconfirm a complete external backup. Continue only from the
fresh authoritative scan. If naming fails again, preserve the new phase/category/
OS-code issue; the prior generic log cannot establish which underlying fix is needed.
