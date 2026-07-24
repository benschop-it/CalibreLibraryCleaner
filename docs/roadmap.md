# Roadmap

## Milestone 0 — Foundation

Solution/projects, package management, nullable types, DI, logging, tests, architecture tests, documentation, and ADRs.

## Milestone 1 — Read-only snapshot

Validate library, read `metadata.db`, load books/authors/identifiers/formats, resolve paths, report missing files, show WPF list, progress, and cancellation.

## Milestone 2 — Hashing and binary duplicates

Streaming SHA-256, bounded concurrency, progress, exact duplicate grouping, side-by-side display.

## Milestone 3 — Exact title/author groups

Deterministic normalization, grouping, reason/confidence display, keyboard navigation, and defer state. No auto-merge.

## Milestone 4 — EPUB assessment

Parser integration, cover/TOC/spine/resource/text checks, reproducible scores, and explainable findings.

## Milestone 5 — Recommendations

Choose metadata and format sources independently, warn about conflicts, support override, and export JSON.

## Milestone 6 — Cleanup plans

Immutable plans, expected states, validation, approval, export/import. Still no mutation.

Implemented as a separate `cleanup-plan/1.0` artifact with immutable semantic bodies, canonical hash-bound approval, terminal stale/blocked/revoked states, guarded external import/export, and WPF review. Backup creation, Calibre tooling, execution, and rollback remain Milestone 7 or later.

## Milestone 7 — Safe execution

Calibre CLI discovery, verified backups, command execution, post-operation verification, and audit history.

Implemented as a single-plan, serial, fail-closed execution workflow for the
exact Calibre 9.11.0 Windows compatibility profile. The supported mutation
mapping is limited to `add_format` additions/replacements and non-permanent
`remove` of redundant source records, after an exclusive application lease,
fresh plan revalidation, and a complete independently hash-verified external
backup. Every mutation uses a direct no-shell process boundary and is followed
by a fresh semantic scan. Hash-chained journals, recovery-required
reconciliation, safe-stop semantics, WPF confirmations, progress, and durable
results are included. Automatic rollback, resume, retry, repair, bulk
execution, metadata transfer, cover replacement, and standalone format removal
remain unsupported.

Post-implementation hardening binds confirmation to canonical root and operation
graph, repeats a complete preflight before every command, prevents verified
executable/backup substitution, and persists an application-local recovery
guard. Cover-bearing plans are blocked until cover bytes are modeled and can be
verified exactly.

The exact 9.11.0 runtime profile remains disabled by default until its opt-in
real-Calibre compatibility suite passes against a caller-marked disposable
test root.

## Milestone 8 — Rollback

Rollback plans, supported restore operations, verification, and history UI.

## Later milestones

PDF analysis, EPUB text fingerprints, optional AI assistance, cover comparison, series normalization, plugins, multi-library comparison, and possible cross-platform UI.
