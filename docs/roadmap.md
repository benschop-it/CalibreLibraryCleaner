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

## Milestone 7 — Safe execution

Calibre CLI discovery, verified backups, command execution, post-operation verification, and audit history.

## Milestone 8 — Rollback

Rollback plans, supported restore operations, verification, and history UI.

## Later milestones

PDF analysis, EPUB text fingerprints, optional AI assistance, cover comparison, series normalization, plugins, multi-library comparison, and possible cross-platform UI.
