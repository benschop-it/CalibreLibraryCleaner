# ADR 0012: Use Persistent Delta-Driven Library State

- Status: Accepted
- Date: 2026-08-02
- Amends: ADR 0007, ADR 0011

## Context

The existing execution and recovery designs repeatedly perform complete library scans. A scan rereads the Calibre catalog, resolves and hashes every format, reassesses ebooks, rebuilds duplicate groups, and regenerates recommendations. Performing that work before and after thousands of mutations makes bulk cleanup impractical.

The product prioritizes bulk-cleanup performance. One explicit full scan is sufficient to establish a library-state generation. After that scan, the application can deterministically project the intended effects of its own typed Calibre commands onto its internal model.

## Decision

`ScanLibraryUseCase` is the only full-scan entry point. A successful explicit scan creates an authoritative library-state generation. That generation remains authoritative across application restarts until the user explicitly scans again.

Every successful typed cleanup or recovery command produces exactly one typed state delta. The application durably commits that delta and applies it to the current immutable state without rereading `metadata.db`, rehashing files, or reinspecting ebooks. Derived duplicate groups, findings, assessments, and recommendations are updated only for affected records and formats.

The application does not automatically detect changes made by Calibre or another process after the baseline scan. The UI must state that external changes require an explicit rescan.

A failed, ambiguous, interrupted, unpersistable, or unprojectable mutating command marks the library-state generation `Uncertain`. No cleanup or recovery mutation is permitted while state is uncertain. Only a successful explicit full scan creates a new authoritative generation and clears uncertainty. There is no automatic full-scan fallback.

The persistence model consists of an immutable baseline or compacted checkpoint, an append-only hash-chained delta journal, and an atomic manifest containing the generation, revision, status, and journal head. Successful replay across restart is mutation-eligible and is not treated as stale merely because it was loaded from persistence.

## Command authority

For this performance-first model, an unambiguous successful typed Calibre command is trusted to have produced its requested effect. Verification means validating and durably applying the corresponding typed delta against the current authoritative revision. It does not mean rereading the Calibre library.

Recovery record creation must return exactly one created Calibre record ID. Missing or ambiguous ID output makes the state uncertain. Other command deltas use record IDs, canonical formats, metadata values, and verified backup fingerprints already present in typed requests.

## Consequences

- A library with thousands of operations performs one full scan followed by bounded state updates.
- External changes remain invisible until explicit rescan and can make projected state differ from Calibre.
- Projected records and formats must distinguish commanded facts from scan-observed physical facts; paths, timestamps, filenames, and Calibre-assigned author IDs must not be invented.
- Existing scan-only persisted snapshots remain view-only. One explicit scan is required to create a delta-capable generation.
- Cleanup, exact-file cleanup, and recovery must migrate together to the same state-session contract.
- Backups, exports, typed Calibre commands, direct-process restrictions, and prohibitions on direct SQLite or managed-file writes remain unchanged.

## Rejected alternatives

- Full scans before or after each command.
- Periodic automatic checkpoint scans.
- Targeted database or filesystem reads after commands.
- Falling back to a scan after an ambiguous command.
- Continuing mutation after the state becomes uncertain.
- Polling `metadata.db` timestamps or other external-change markers.