# Safety and Rollback

## Analysis mode

Allowed: read database and files, hash, parse, and create application-owned reports/cache outside the library.

Forbidden: rename, move, overwrite, delete, change metadata, add formats, or create Calibre-managed files.

PDF assessment treats every document as untrusted. It validates root containment and rejects reparse points, opens read-only with restrictive sharing, checks the expected observation and SHA-256, and rechecks identity before returning. Signature/trailer, file, object, stream, page, sample, text, image, pixel, operation, dimension, metadata, XMP, outline, finding, protocol, wall-time, CPU, heap, and working-set bounds fail closed.

PDF parsing runs in a disposable per-file worker. The parent terminates the process tree on cancellation, timeout, CPU, or memory breach; Windows uses a kill-on-close single-process Job Object when available. Production code never invokes PDF action, hyperlink, attachment, rendering, image-decoding, OCR, network, or external-viewer APIs. Only bounded aggregates, short normalized fingerprints, short metadata, and validated identifiers survive; PDF text and embedded content are never logged.

PDF findings and scores are analysis evidence only. They cannot approve or trigger cleanup, mutation, rollback, or recovery. Milestone 7 execution and Milestone 8 recovery behavior is unchanged.

Milestone 5 recommendation review JSON is an application-owned analysis artifact, not a cleanup plan. It may be written only to an explicitly selected existing directory outside the Calibre library, using a guarded temporary sibling and publish step. It contains generated/reviewed evidence and staleness, but no removal, command, approval, backup, mutation ordering, or expected pre-operation state. The exporter rejects the library root, descendants, and reparse-point destinations.

## Plan validation

Before execution verify library identity, record existence, paths, file hashes, format state, target validity, conflicts, backup destination, and Calibre tool availability. Any mismatch invalidates the plan.

Milestone 6 records these expected states and backup requirements but performs no execution-time verification or backup. Cleanup plans are generated only from a current accepted or manually adjusted recommendation, remain non-executable, and may be approved or revoked only as immutable data. Imported plans are untrusted: schema, bounds, paths, graph coverage, lifecycle, and canonical hashes are validated, and current snapshot mismatch makes a plan stale. A readable future policy is retained only as blocked.

Exact-binary groups spanning multiple records may independently produce a single-keeper plan. Selecting a keeper automatically targets every other distinct group record for deletion; no independent deletion checkboxes exist. Generation captures complete involved-record state. Before deletion, the executor copies and rehashes every involved format, exports each complete record through Calibre, verifies OPF/cover/format coverage, and seals an external manifest. It then uses non-permanent `calibredb remove` for every non-keeper ID and performs a complete scan after every command. The bundle or the user's full library copy is the recovery source; automatic recovery is not required for this workflow.

The stored analysis snapshot is deleted after final confirmation and before the first record removal. Failure to invalidate it blocks mutation. Verification scans do not replace it because they are not guaranteed to include every normal analysis phase; the user runs a fresh normal scan to create the next persisted snapshot.

Cleanup-plan import/export is explicit and restricted to `.cleanup-plan.json` files outside the physically resolved selected library. Export uses an external temporary sibling and publication step; import is bounded and read-only. Neither operation creates a plan, temporary file, cache, lock, or backup inside the library.

## Backup

Back up all formats, cover, exported metadata/OPF where available, original paths, hashes, sizes, timestamps, identifiers, plan, and execution log. Verify backup hashes before mutation.

## Execution order

1. Acquire operation lock.
2. Revalidate plan.
3. Create and verify backup.
4. Invoke supported Calibre operations.
5. Capture output and exit status.
6. Reload the library.
7. Verify metadata, formats, paths, and hashes.
8. Persist audit result.

Milestone 7 implements this order with two complete pre-mutation scans, a
write-ahead mutation marker, constructive format operations, an explicit
destructive gate, record removals last, and a complete read-only scan after every
Calibre command. A complete scan, lease check, immutable plan/graph check, tool
identity check, backup recheck, and confirmation check also run immediately
before every command. The local recovery guard is durable before the first
mutation marker. Only exact-profile typed `calibredb` operations are allowed.
Cancellation after mutation starts is a safe-stop request between verified
operations; the active Calibre process is never killed. Any uncertain partial
state is durably marked `RecoveryRequired`.

V1 does not hash cover content in the library snapshot. Therefore any plan
whose involved records report a cover is unsupported and blocks before backup
or mutation; cover preservation must not be inferred from presence alone.

## Rollback

Rollback is a first-class verified operation that restores records, metadata, formats, and covers through supported mechanisms. It must not rely only on `.caltrash`.

Milestone 8 implements recovery as a separately generated and explicitly
approved immutable plan. Eligibility strictly reloads and cross-checks the
Milestone 7 cleanup plan, hash-chained journal, any journal-proven terminal
summary, manifest, and every original backup item. Nonterminal source execution
is recoverable without inventing a summary; an orphan summary without a
terminal journal event is ignored. A complete fresh read-only scan is reconciled
against verified pre-state and durable execution progress. Unknown journal
state, identity mismatch, ambiguity, unsupported capability, or potential
silent data loss blocks recovery.

Before mutation, recovery creates a distinct versioned backup of every current
affected record. It contains raw current formats, strict Calibre exports,
metadata, covers, inventory and fingerprints, the approved recovery plan, and
new audit copies of the source plan, journal, summary, and original manifest.
The original Milestone 7 bundle is opened read-only and never changed. Every
new manifest entry and both manifest hashes are independently reverified.
Affected semantic inventory and strict Calibre OPF exports must agree before
the backup can authorize mutation.

Recovery then executes in this order:

1. Hold the shared cleanup/recovery lease and repeat source, tool, plan, and
   current-state checks.
2. Create and verify the current-state backup.
3. Apply constructive operations serially through typed Calibre commands.
4. Rescan after every command and verify restored hashes and semantic identity.
5. Verify all constructive and preservation expectations.
6. Obtain a separate confirmation bound to the exact destructive graph.
7. Apply approved destructive operations last, without automatic retry.
8. Perform final semantic verification, including unrelated and preserved data.
9. Finalize every scan-discovered record-ID mapping with the verified formats
   and identifiers, then persist the terminal journal, history, and resolution
   link.

An unplanned format on an affected record, collateral affected-record metadata
change, preservation mismatch, or unrelated-record change blocks semantic
success. A process exit code of zero records transport success only; every
mutation still requires a fresh full read-only scan and semantic verification.

Cancellation before mutation is immediate. During backup it prevents mutation.
After mutation begins it is a safe-stop request honored only after the active
Calibre process finishes and its effect is scanned. Recovery never kills a
mutation process, resumes automatically, retries destructive work, or performs
a rollback of a rollback. `Recovered` requires final semantic success;
constructive partial results report `PartiallyRecovered`, final mismatch reports
`VerificationFailed`, and destructive or indeterminate failure reports
`ManualInterventionRequired`.

Cover restoration and every exact Calibre mutation capability remain disabled
unless separately qualified. The production 9.11.0 recovery profile is
therefore fail-closed by default.

## Concurrency

Only one cleanup or recovery operation may hold the shared library-mutation
lease at a time. Warn the user not to run Calibre or other library-mutating
tools concurrently.
