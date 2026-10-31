# Continue after verified-unchanged managed-name rejections

## Objective

Finish managed naming for other books when a supported Calibre update is rejected
and complete fresh read-back proves that the affected book is unchanged.

## Scope

Extend the existing verified-unchanged metadata-rejection pattern to managed
naming. Report skipped records separately, preserve their projected state, and
leave them unverified/pending in final disposition while other books proceed.

## Out of scope

Continuing after changed, unreadable, ambiguous or interrupted mutation; retry,
rollback, recovery, success-prefix inference, mutation fallbacks, cache invalidation
or mutating the user's library for diagnosis.

## Relevant requirements

- Current user request: a few per-book rejections must not stop all useful work.
- [Safety](../../safety-and-rollback.md): verified-unchanged rejection is not failed
  mutation; ambiguous/changed outcomes still stop and require Scan.
- [Architecture](../../architecture.md): fixed supported-API worker and typed projection.
- [ADR 0021](../../adr/0021-matching-first-performance-oriented-product.md): throughput
  and simple bounded mutation, without retry/recovery.

## Existing implementation inspected

Metadata already catches rejected writes, proves complete unchanged state and emits
a successful typed skip/no-op delta. Managed naming instead aborts on every API
exception. Its pre/post snapshot omits the managed path and record timestamps;
those must be included before authorizing a verified-unchanged rejection.
Execution counts all naming operations as refreshed and all skips as metadata;
qualification uses a separate verified naming ID set that must exclude skips.

## Proposed design

Capture full pre-state including path, identity/timestamps, ordered author sorts,
registered format paths/names/hashes/observations, cover, local/custom values,
extra files and composites. If `update_path` raises, clear caches and read once;
continue only on exact complete equality. No update retry. Pre-read failure,
unreadable post-state or any difference remains a failed chunk.
On Windows, use a read-only contained file-handle delete-sharing access probe first;
it opens existing files without writing/deleting and closes every handle immediately.
A rejected probe receives the same fresh unchanged-state proof before skipping.

Emit skipped refresh operations with a technical reason and no fabricated naming
read-back. Advance the journal via a typed no-op naming-skip delta. Add separate
skipped-naming results, counts/IDs/reasons, logging and WPF summary. Exclude skipped
IDs from refreshed count and verified naming evidence. Version the fixed worker
protocol for the new outcome, without changing analysis/provider cache versions.

## Files expected to change

Worker/protocol and controlled worker fixtures, Domain delta and JSON projection,
Application counting/contracts, WPF reporting, focused tests and current docs.

## Safety considerations

Only read-back-proven unchanged rejection may continue. Keep the existing failure
marker and stop boundary for all changed/unverifiable outcomes. Never claim a
skipped book's managed naming was verified or auto-transfer it for missing coverage.
No exception messages, metadata or paths in new summaries/logs.

## Implementation steps

1. Reproduce rejected-update abort and freeze unchanged versus changed/unreadable
   worker cases.
2. Add verified-unchanged worker skip and strict protocol support.
3. Persist typed no-op naming skips and wire counts/IDs/qualification/UI.
4. Validate multi-chunk continuation, real generated Windows lock, negative
   mutation boundaries and normal Visual Studio outputs.

## Tests

Full-state equality for each protected component; changed/unreadable rejection
stops. Unchanged rejection at operation 59 continues through later books/chunks
without retry. Naming skips retain snapshot identity across delta round-trip,
do not inflate metadata/refreshed counts and never enter verified naming IDs.
Real generated Calibre Windows lock skips only its unchanged book and later naming
still executes. WPF reports partial success and allows disposition review.

## Verification commands

Generated worker checks and real disposable-library acceptance; focused
Domain/Application/Infrastructure/WPF tests; normal build, full suite, format,
whitespace and dependency vulnerability verification.

## Risks

Stale caches or omitted state could falsely authorize continuation. Clear the
affected record's caches and compare complete snapshots, not only title/bytes.
Protocol readers must reject malformed skips and older incompatible worker versions.

## Unresolved questions

None. Use no arbitrary skip quota: every record must independently satisfy the same
proof. A single ambiguous/changed record still stops mutation.

## Progress

- [x] Traced existing metadata skip pattern, naming pipeline and qualification gate.
- [x] Reproduced unchanged-update abort before implementation.
- [x] Implemented protocol 1.7 naming skips, typed no-op journal and separate counts,
  IDs/reasons, logging and WPF pending/acceptance behavior.
- [x] Real Calibre testing exposed two facts: successful naming updates
  `last_modified` legitimately, while rejected Windows renames may alter format
  attributes. Normal success now allows only the documented timestamp side effect;
  unchanged proof still compares everything. A read-only sharing probe rejects
  existing locks before Calibre can alter file attributes.
- [x] Real generated acceptance now skips the locked book and renames the following
  book in the same chunk, without retry and with all four records/formats preserved.
- [x] Focused Domain/Application/protocol/journal/WPF checks passed. WPF initially
  exposed an enabled reviewer-acceptance button for unverified layout; its predicate
  now shares the Application precondition instead of inviting a rejected action.
- [x] Actual generated quarantine acceptance detected that naming-only record
  identity/timestamp evidence must remain separate from copied-content comparison.
  Isolated that evidence; real quarantine copy/delete and naming continuation both
  passed under protocol 1.7. Added a comparison regression, without weakening the
  unchanged naming proof.
- [x] Final normal Debug build passed with zero warnings/errors. Full .NET suite:
  Domain 492, Application 327, Infrastructure 387, Architecture 34, WPF 99;
  1,339 passed, zero skipped.
- [x] Eleven generated Python worker checks, actual four-book Calibre naming
  acceptance and actual quarantine copy/delete acceptance passed under 1.7.
- [x] Full formatting verification, whitespace check and dependency vulnerability
  audit succeeded. No dependency changes, alternate outputs, user library writes,
  forced process termination or cache invalidation.
- [x] Reconciled current documents and archived the completed plan.

## Final outcome

Managed naming now continues after independently verified unchanged per-book
rejections. An already locked Windows format is rejected read-only before Calibre
can alter its attributes; a detached complete pre/post comparison still gates the
skip. Later books in that chunk and subsequent chunks run without retrying the
skipped update. Proven skips use a persisted no-op outcome, explicit technical
IDs/reasons, separate counters and an unverified/pending final-disposition state.
The user can proceed with other records without a restart or Scan for these skips.

Changed/unreadable/ambiguous states retain the existing immediate stop and explicit
Scan boundary. This does not claim that every exception can be ignored, or recover
the cause of an already logged generic failure. A successful naming operation may
change its intended path/basename and Calibre's dirty timestamp; a rejection may
not hide those changes. No rollback, automatic retry or inferred successful prefix
was introduced.

The normal Visual Studio F5 outputs include the updated embedded protocol-1.7
worker. The prior production run is already uncertain, so that one state still
requires **F5 -> select the same library -> Scan, not Load** before following the
fresh workflow and confirming the external backup for any mutation. Future proven
unchanged naming skips no longer require that restart/Scan cycle.
