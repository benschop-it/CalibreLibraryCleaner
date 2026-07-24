# Test Strategy

Use xUnit, FakeItEasy, and FluentAssertions.

## Test levels

- Domain unit tests: invariants, normalization, scores, confidence, recommendations, and plans.
- Application tests: validation, cancellation, missing files, grouping, recommendation, stale plans, backup ordering, and verification failures.
- Infrastructure integration tests: read-only SQLite, paths, hashes, malformed EPUBs, process wrapper, JSON storage, and backups.
- Architecture tests: dependency direction and prohibited references.
- Focused UI tests: library choice, scan cancellation, group navigation, overrides, warnings, and approval.

## Fixtures

Generate synthetic temporary Calibre-style libraries for empty, valid, binary duplicate, conflicting EPUB, missing file, malformed EPUB, missing cover/TOC, conflicting ISBN, non-conflicting formats, and stale-plan scenarios.

## Safety assertions

Analysis must not modify database bytes, file timestamps, names, or contents and must not create files inside the library. Execution tests must prove backup precedes mutation, backup failure prevents mutation, stale preconditions block execution, and verification always occurs.

Milestone 6 tests cover cleanup-plan eligibility failures, no-silent-loss and backup coverage, canonical hashing, immutable lifecycle transitions, approval/revocation binding, staleness, deterministic JSON round trips, malformed/future/unsafe imports, external-only storage, cancellation, architecture boundaries, WPF presentation, and recursive synthetic-library manifests. These tests must not introduce or exercise execution behavior.

Milestone 7 tests use faked Application ports for orchestration and controlled
helper executables for process invocation. They prove exact-version capability
rejection, backup-before-mutation, external-only path guards, manifest
rehashing, lease exclusion, write-ahead journaling, constructive-before-
destructive ordering, fresh semantic verification after every command,
fresh per-command preflight, confirmation root/graph binding, executable and
backup path-substitution resistance, reparse-point rejection, safe-boundary
cancellation, and durable recovery-required results. Journal tests require an
agreeing immutable terminal summary after mutation, and cover-bearing plans
must fail closed. Real-Calibre tests are opt-in and create only a caller-supplied
disposable library; they never discover or use a default/user library.
