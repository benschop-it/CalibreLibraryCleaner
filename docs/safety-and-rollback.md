# Safety and Rollback

## Analysis mode

Allowed: read database and files, hash, parse, and create application-owned reports/cache outside the library.

Forbidden: rename, move, overwrite, delete, change metadata, add formats, or create Calibre-managed files.

Milestone 5 recommendation review JSON is an application-owned analysis artifact, not a cleanup plan. It may be written only to an explicitly selected existing directory outside the Calibre library, using a guarded temporary sibling and publish step. It contains generated/reviewed evidence and staleness, but no removal, command, approval, backup, mutation ordering, or expected pre-operation state. The exporter rejects the library root, descendants, and reparse-point destinations.

## Plan validation

Before execution verify library identity, record existence, paths, file hashes, format state, target validity, conflicts, backup destination, and Calibre tool availability. Any mismatch invalidates the plan.

Milestone 6 records these expected states and backup requirements but performs no execution-time verification or backup. Cleanup plans are generated only from a current accepted or manually adjusted recommendation, remain non-executable, and may be approved or revoked only as immutable data. Imported plans are untrusted: schema, bounds, paths, graph coverage, lifecycle, and canonical hashes are validated, and current snapshot mismatch makes a plan stale. A readable future policy is retained only as blocked.

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

Rollback execution is not part of Milestone 7. Milestone 7 preserves the
verified recovery bundle and reports recovery requirements but never restores,
retries, or resumes automatically.

## Concurrency

Only one cleanup operation may execute at a time. Warn the user not to run other library-mutating operations concurrently.
