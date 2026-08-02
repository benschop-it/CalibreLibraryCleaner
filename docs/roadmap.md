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
capability-probed Calibre 9.x Windows compatibility profile. The supported mutation
mapping is limited to `add_format` additions/replacements and non-permanent
`remove` of redundant source records, after an exclusive application lease,
authoritative-revision plan validation, and a complete independently hash-verified external
backup. Every mutation uses a direct no-shell process boundary and durably commits a typed state delta. Hash-chained journals, recovery-required
reconciliation, safe-stop semantics, WPF confirmations, progress, and durable
results are included. Automatic rollback, resume, retry, repair, bulk
execution, metadata transfer, cover replacement, and standalone format removal
remain unsupported.

Post-implementation hardening binds confirmation to canonical root and operation
graph, validates the current projected revision before every command, prevents verified
executable/backup substitution, and persists an application-local recovery
guard. Cover-bearing plans are blocked until cover bytes are modeled and can be
verified exactly.

The 9.11-or-newer Calibre 9.x cleanup runtime profile is enabled by default and still requires
executable identity, a compatible version, command probes, authoritative state, and typed command
mapping. Recovery capabilities remain independently disabled unless qualified.

## Milestone 8 — Rollback

Rollback plans, supported restore operations, verification, and history UI.

Implemented as a single-execution, explicitly approved, reconciliation-driven
recovery workflow. It strictly reverifies Milestone 7 source artifacts, uses
the authoritative projected state, generates a canonical immutable recovery plan, preserves or
blocks on unexpected current data, creates and verifies a separate current-state
backup, restores constructively before separately confirmed destructive work,
commits a typed delta after every successful command, verifies the final projected state, and persists
hash-chained journals, append-only history, source-resolution links, and changed
record-ID mappings. Cleanup and recovery share one lease domain. No direct
database or managed-library filesystem mutation, shell invocation, automatic
retry, resume, bulk recovery, or rollback-of-rollback is included.

Calibre 9.x recovery mutation capabilities remain disabled by
default until the opt-in disposable-library qualification passes per
capability. Unsupported cover restoration and ambiguous or data-losing cases
remain manual-intervention blockers.

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

## Milestone 11 — Expanded duplicate discovery

Add strong normalized identifier candidate groups and conservative fuzzy
metadata discovery for cases not found by exact normalized title/author
matching. Combine independent binary, metadata, identifier, and content signals
as explainable evidence while preserving each signal's provenance and
confidence. Fuzzy candidates remain manual-review work and never authorize
automatic deletion.

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

## Possible later platform work

Cross-platform UI evaluation may follow after the Windows WPF safety and review
workflows are complete. It requires a separate architecture decision because
worker containment, filesystem identity, reparse/link handling, process
durability, and Calibre integration differ by operating system.
