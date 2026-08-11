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

## Milestone 6 — Cleanup plans (superseded)

The immutable cleanup-plan artifact and UI were implemented, then removed by ADR 0017. Recommendation review remains analysis-only; there is no general executable consolidation-plan workflow.

## Milestone 7 — Safe execution (superseded)

The general direct-command execution, application-created backup, journal, and history stack were implemented, then removed by ADR 0017. Exact binary cleanup now uses only the constrained persistent worker and requires per-run confirmation of a user-owned external backup.

## Milestone 8 — Rollback (superseded)

Automated reconciliation-driven recovery was implemented, then removed by ADR 0017. Mutation failures are logged, stop immediately, mark projected state Rescan-required, and rely on the user's external full-library backup for manual restoration if needed.
## Milestone 9 — PDF assessment

Safe read-only PDF assessment with an isolated PdfPig worker, deterministic bounded sampling, separate explainable classification, PDF-specific findings-derived scoring, bounded text/image/metadata/identifier evidence, resource enforcement, progress/cancellation, and WPF presentation. PDF assessment does not rank retained PDFs or change cleanup, execution, or recovery behavior.

Implemented with analyzer `pdf-inspector/1.0.0`, scoring model
`pdf-quality/1.0.0`, classification policy `pdf-classification/1.0.0`, sampling
policy `pdf-sampling/1.0.0`, resource profile `pdf-limits/1.0.0`, and worker
protocol `pdf-worker-protocol/1.0`. Automated verification is complete. The
manual WPF acceptance plan remains the final acceptance activity.

## Exact-binary record cleanup

Exact file groups automatically retain the best located same-format copy using record format count, metadata/identifier/cover completeness, and record ID as the final tie-breaker. Execution creates and verifies complete external record backups, obtains final confirmation, calls typed `calibredb remove_format` for duplicate copies, and calls non-permanent `calibredb remove` only for records projected empty. Successful commands update authoritative state through durable deltas without rescanning.

## Milestone 10 — Content fingerprints and comparisons

Versioned, bounded, deterministic EPUB and PDF content fingerprints and
cross-file comparison evidence. EPUB work starts from spine-order visible text
with strict and punctuation-insensitive identities. PDF work must distinguish
all-page evidence from bounded sampled evidence and cannot claim whole-document
equivalence from a sample alone. Results expose coverage, compatibility,
confidence, and reasons in WPF without retaining book prose.

Milestone 10 is analysis and comparison only. It does not silently treat
different editions, languages, translations, abridgements, illustrated works,
or scan variants as interchangeable; does not automatically select a retained
format; and does not alter cleanup, execution, or recovery authority.

Implemented first slice: candidate-only EPUB visible-text signatures with 12 bounded hash landmarks, a bounded shifted-text sketch, fingerprint/version cache, and symmetric review evidence. PDF cross-document signatures remain pending.

## Milestone 11 — Expanded duplicate discovery

Add strong normalized identifier candidate groups and conservative fuzzy
metadata discovery for cases not found by exact normalized title/author
matching. Combine independent binary, metadata, identifier, and content signals
as explainable evidence while preserving each signal's provenance and
confidence. Fuzzy candidates remain manual-review work and never authorize
automatic deletion.

Implemented local deterministic first slice under ADR 0019: bounded indexed candidate generation, explicit contradictions, constrained stable-anchor clustering, language partition from catalog/OPF evidence, persisted review-only groups, scan progress/metrics, and a separate WPF tab. Content-language detection and optional enrichment remain pending calibration.

## Milestone 12 — Visual and metadata review

Add bounded cover preview/comparison, richer side-by-side format evidence,
external file opening through an explicit safe user action, and deterministic
series/metadata normalization suggestions. Preserve original metadata and cover
bytes in every later approved cleanup/backup path before any new recommendation
can become executable.

## Milestone 13 — Optional AI assistance

Add opt-in advisory AI for ambiguous metadata or edition comparison only after
deterministic evidence and local review are mature. Require explicit consent,
minimal disclosed content, provider/model provenance, bounded requests, and a
clear separation between AI confidence and deterministic confidence. AI output
cannot approve a plan or trigger mutation.

## Milestone 14 — Extensibility and multi-library workflows

Add reviewed plugin boundaries, multi-library comparison, durable analysis
caching with versioned invalidation, and scale-oriented workflow improvements.
Plugins receive least authority and cannot bypass cleanup-plan, backup,
verification, or recovery gates.

## Staged exact-first candidate cleanup

ADR 0020 supersedes future development of the one-scan composite workflow. The
migration first adds durable workflow state, exact-only analysis, and an `Exact
cleanup` primary action that composes the existing Exact algorithm unchanged.
Trusted post-exact reconciliation, unified residual discovery, Candidate cleanup,
and shadow parity are implemented. `Cleanup all` and standalone Metadata/Expanded
mutation surfaces are retired; Metadata/Expanded analysis views remain available.

## Possible later platform work

Cross-platform UI evaluation may follow after the Windows WPF safety and review
workflows are complete. It requires a separate architecture decision because
worker containment, filesystem identity, reparse/link handling, process
durability, and Calibre integration differ by operating system.
