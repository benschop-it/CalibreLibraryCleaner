# Safety and Rollback

## Analysis mode

Allowed: read database and files, hash, parse, and create application-owned reports/cache outside the library.

Forbidden: rename, move, overwrite, delete, change metadata, add formats, or create Calibre-managed files.

## Plan validation

Before execution verify library identity, record existence, paths, file hashes, format state, target validity, conflicts, backup destination, and Calibre tool availability. Any mismatch invalidates the plan.

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

## Rollback

Rollback is a first-class verified operation that restores records, metadata, formats, and covers through supported mechanisms. It must not rely only on `.caltrash`.

## Concurrency

Only one cleanup operation may execute at a time. Warn the user not to run other library-mutating operations concurrently.
