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

Analysis must not modify database bytes, file timestamps, names, or contents and must not create files inside the library.

Duplicate-cleanup tests prove deterministic keeper overrides, metadata Skip behavior, external-backup acknowledgement, one worker process, chunks of at most 100 operations, complementary transfers before source removals, record removals last, complete-chunk projection, typed delta durability, lease exclusion, checkpointing, and no direct-command fallback. Metadata tests prove generated metadata-source keepers, keeper-authoritative non-identical same-format removal, and preflight skipping for unresolved complementary sources. Worker startup failures stop before mutation. Failed or ambiguous chunks emit structured logs, do not project successful prefixes, mark state uncertain, and block later mutation until explicit Scan.

Persistence tests prove metadata-only legacy listing, manifest-only state listing, strict full snapshot loading, baseline/delta replay, hash-chain tamper detection, uncertainty persistence, bounded run markers, checkpoint compaction, authoritative restart loading, current-generation pruning, and legacy migration after atomic publication. A deterministic 10,000-record/6,000-delta test verifies projection without a wall-clock threshold.

Infrastructure worker tests use only temporary caller-created disposable libraries and controlled executables. They validate trusted executable/script identity, fixed protocol messages, bounded I/O, writer-process rejection, handshake capabilities, cancellation/timeouts, and no direct SQLite or managed-file mutation. Ordinary automated tests never discover or use a default or personal Calibre library.

WPF tests cover persisted development loading, exact keeper overrides, backup confirmation, progress/results, state uncertainty wording, XAML activation, and close protection during exact cleanup. Architecture tests prohibit recovery, cleanup-plan, app-backup/history, and direct mutation gateway boundaries from returning.

Viewer tests use a controlled sibling executable and temporary synthetic library files. They prove exact one-argument launch, no-shell process configuration, trusted sibling discovery, containment/reparse validation, missing viewer/file outcomes, exact-row paths, metadata format preference, WPF command routing, and no cleanup-state mutation.