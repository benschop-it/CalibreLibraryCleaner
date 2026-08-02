# Test Strategy

Use xUnit, FakeItEasy, and FluentAssertions.

## Test levels

- Domain unit tests: invariants, normalization, scores, confidence, recommendations, and plans.
- Application tests: validation, cancellation, missing files, grouping, recommendation, stale plans, backup ordering, and verification failures.
- Infrastructure integration tests: read-only SQLite, paths, hashes, malformed EPUBs/PDFs, isolated process wrappers, JSON storage, and backups.
- Architecture tests: dependency direction and prohibited references.
- Focused UI tests: library choice, scan cancellation, group navigation, overrides, warnings, and approval.

## Fixtures

Generate synthetic temporary Calibre-style libraries for empty, valid, binary duplicate, conflicting EPUB, missing file, malformed EPUB/PDF, missing cover/TOC/outline, conflicting ISBN, non-conflicting formats, and stale-plan scenarios. PDF fixtures are programmatically generated with PdfPig's writer or small explicit object graphs; no copyrighted or personal library content is used.

PDF coverage includes digital text, image-only scan evidence, existing text layers, mixed/illustrated content, metadata/outline variants, valid/invalid ISBN evidence, encryption/password-required, zero/truncated/non-PDF/malformed/zero-page inputs, resource families, deterministic sampling/classification/scoring/finding order, penalty caps, disqualification, worker timeout/cancellation/no-orphan behavior, bounded concurrency, prohibited network/action/attachment/OCR/image-decode APIs, no full-text retention or content logging, read-only library manifests, and parser-type architecture boundaries.

## Safety assertions

Analysis must not modify database bytes, file timestamps, names, or contents and must not create files inside the library. Execution tests must prove backup precedes mutation, backup failure prevents mutation, stale preconditions block execution, and verification always occurs.

Milestone 6 tests cover cleanup-plan eligibility failures, no-silent-loss and backup coverage, canonical hashing, immutable lifecycle transitions, approval/revocation binding, staleness, deterministic JSON round trips, malformed/future/unsafe imports, external-only storage, cancellation, architecture boundaries, WPF presentation, and recursive synthetic-library manifests. These tests must not introduce or exercise execution behavior.

Milestone 7 tests use faked Application ports for orchestration and controlled
helper executables for process invocation. They prove exact-version capability
rejection, backup-before-mutation, external-only path guards, manifest
rehashing, lease exclusion, write-ahead journaling, constructive-before-
destructive ordering, durable typed-delta commits after successful commands,
projected per-command validation, confirmation root/graph binding, executable and
backup path-substitution resistance, reparse-point rejection, safe-boundary
cancellation, and durable recovery-required results. Journal tests require an
agreeing immutable terminal summary after mutation, and cover-bearing plans
must fail closed. Real-Calibre tests are opt-in and create only a caller-supplied
disposable library; they never discover or use a default/user library.

Milestone 8 adds Domain tests for immutable recovery bodies, canonical hashes,
dependency gates, lifecycle rules, final-verification invariants, and changed
record IDs. Application tests use faked ports and mutable synthetic snapshots
to prove three-way reconciliation, mismatch and unexpected-data
classification, warning-bound approval, current-backup-before-mutation,
constructive-before-destructive ordering, safe cancellation, no retry,
missing created IDs, delta-commit failure, partial recovery, destructive failure, final
verification, and durable mappings.

Infrastructure recovery tests use temporary directories, synthetic Milestone 7
bundles, controlled helper executables, strict plan round trips, manifest
rehashing, source audit copies, append-only history, journal crash/terminal
reconciliation, shared lease exclusion, and fixed no-shell command arguments.
Adversarial cases cover source-bundle substitution, orphan terminal summaries,
nonterminal recovery sources, source and current-backup tampering, reparse
points, semantic OPF mismatch, journal event ordering, missing or substituted
final record-ID mappings, collateral metadata changes, unexpected affected
formats, unrelated-record changes, destructive target drift, nonzero exits,
post-mutation journal failure, and terminal persistence gaps.
WPF tests require individual warning acknowledgements and accurate immutable
approval state. Architecture tests prohibit filesystem/process/JSON/UI leakage
into recovery core layers, direct alternate process boundaries, shell use, and
automatic rollback/resume/bulk recovery.

ADR 0012 tests prove baseline/delta replay, hash-chain tamper detection, uncertainty persistence, checkpoint compaction, authoritative restart loading, revision publication, and zero scanner abstractions in mutation workflows. A deterministic 10,000-record/6,000-delta test verifies state projection without a wall-clock threshold.

Real-Calibre recovery qualification is opt-in only. It requires an exact
explicit executable and caller-marked disposable test root and must qualify
each capability before that capability can be enabled in production. The
ordinary automated suite never discovers Calibre's default library or accepts a
production library path.
