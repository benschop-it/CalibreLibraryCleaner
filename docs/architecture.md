# Architecture

## Projects

```text
src/
  CalibreLibraryCleaner.Domain/
  CalibreLibraryCleaner.Application/
  CalibreLibraryCleaner.Infrastructure/
  CalibreLibraryCleaner.PdfWorker/
  CalibreLibraryCleaner.Wpf/

tests/
  CalibreLibraryCleaner.Domain.Tests/
  CalibreLibraryCleaner.Application.Tests/
  CalibreLibraryCleaner.Infrastructure.Tests/
  CalibreLibraryCleaner.Architecture.Tests/
  CalibreLibraryCleaner.Wpf.Tests/
```

## Dependency direction

```text
Domain <- Application <- Infrastructure
                    <- Wpf
```

`Wpf` may reference `Infrastructure` only in the composition root.

## Responsibilities

- Domain: books, formats, identities, duplicate groups, findings, scores, recommendations, plans, invariants.
- Application: use cases and integration interfaces.
- Infrastructure: read-only SQLite, paths, hashing, ebook inspection, Calibre CLI, backups, JSON storage, analysis cache.
- WPF: selection, progress, comparison, review, approval, history, and settings.
- WPF tests: focused ViewModel and presentation-state behavior on the Windows target.

## Long-running operations

Must be asynchronous, cancellable, progress-reporting, bounded in parallelism, and non-blocking to the UI.

## Persisted analysis snapshot boundary

Application owns the `ILibrarySnapshotStore` port and list/load/save outcomes. Infrastructure owns the versioned, bounded JSON representation and atomic latest-only storage under the user's local application-data directory. Canonical library folder paths are keys; their SHA-256 digests are filenames, and the embedded path is revalidated on read. Snapshot artifacts are never written inside a Calibre library.

WPF lists persisted library paths at startup, loads only after an explicit user command, and automatically replaces the cached result after a complete successful scan. Loaded snapshots are historical review evidence: cleanup-plan, execution, and recovery contexts remain cleared until a fresh scan supplies live state. Existing execution and recovery preflight rules always perform fresh scans before mutation.

Mutation verification scans are not persisted because execution may intentionally omit expensive assessment phases such as PDF assessment. Exact-binary record deletion invalidates the cached snapshot before the first mutation. The next normal full scan recreates a complete cache rather than synthesizing one from command effects.

## EPUB inspection boundary

Application owns the provider-neutral `IEpubInspector` contract, inspection limits/results, deterministic scoring engine, and bounded orchestration. Infrastructure alone owns ZIP, XML, HTML, image-header, filesystem, and VersOne/Html Agility Pack types. EPUB files are preflighted and opened read-only, content is never extracted or fetched, expected untrusted-input failures become structured inspection problems, and the final snapshot is published only after all assessments complete.

## PDF inspection boundary

Application owns the provider-neutral `IPdfInspector` contract, deterministic page sampling, classification, scoring, progress, cancellation, and bounded library orchestration. Infrastructure alone owns PdfPig 0.1.15, PDF tokens/filters, read-only file handles, SHA-256 revalidation, the versioned JSON protocol, and worker-process containment. WPF deploys a fixed sibling worker executable; paths are protocol data, never process arguments. Each file gets a fresh worker with a cleared environment, managed-heap limit, Windows Job Object where available, and parent wall-time, CPU, and working-set watchdogs.

The worker opens one seekable read-only stream, reports the page header, accepts the Application-selected bounded sample, and returns provider-neutral aggregates. It does not render, OCR, execute actions, follow links, access network resources, or extract attachments. Encoded streams are checked before decode; decoded and aggregate outputs are checked immediately afterward, while process containment covers decoder allocations made before control returns. Parser failures become closed problem codes without paths, text, binary content, or raw exceptions.

`FormatAssessment` is the shared result/identity core. `EpubAssessment` and `PdfAssessment` add format-specific feature semantics. PDF classification is separately versioned and never feeds the score. PDF assessments are a separate `LibrarySnapshot` collection and are not inputs to recommendations, cleanup plans, execution, or recovery.

## Recommendation and review-export boundary

Domain owns immutable recommendation selections, reasons, warnings, decision strength, qualitative confidence, review values, and invariants. Application indexes completed Milestone 2â€“4 evidence, orchestrates deterministic generation, validates overrides, evaluates staleness, and owns the external export port. Infrastructure alone owns JSON parsing/serialization and guarded file publication outside the selected library. WPF owns session review interaction and file selection; ViewModels call Application use cases and do not write files. Recommendation review artifacts contain no cleanup-plan or mutation instructions.

## Cleanup-plan boundary

Domain owns immutable cleanup-plan bodies, expected library/record/format state, declarative retention and removal intentions, backup requirements, provenance, validation issues, canonical semantic hashes, and lifecycle transition rules. Application owns explicit generation from one current reviewed recommendation, current-snapshot validation, staleness, approval, revocation, and import/export orchestration. Infrastructure alone owns `cleanup-plan/1.0` JSON parsing/serialization and guarded external file storage. WPF owns review and confirmation interaction; ViewModels do not serialize or access files.

Cleanup plans are non-executable data. Milestone 6 introduces no Calibre process, command, backup creator, lock, mutation, simulation, or rollback boundary.

Exact-binary record cleanup uses a separate plan and compact executor because metadata-consolidation plans choose metadata and format sources for an entire candidate group. WPF owns keeper selection, per-record deletion checks, approval, backup destination, and final confirmation. Application performs fresh scans before every typed non-permanent `calibredb remove` and verifies each record absence plus keeper/unrelated-state preservation afterward. Infrastructure creates a complete external bundle of raw formats and Calibre exports, seals a hash manifest, and appends the execution audit.

## Safe execution boundary

Milestone 7 keeps cleanup plans immutable and introduces a separate execution
aggregate. Domain owns lifecycle, operation dependencies, backup-manifest
values, verification results, and recovery invariants. Application owns live
preflight, backup sequencing, constructive/destructive gates, cancellation
policy, and serial orchestration. Infrastructure alone owns Calibre discovery,
the exact-version typed command mapping, direct process invocation, external
backup and journal files, execution leases, free-space checks, and crash
reconciliation. WPF owns explicit confirmation, progress, safe-stop requests,
and accurate terminal-state presentation.

Every mutation uses direct `calibredb` invocation with a fixed executable and
argument list. No shell, GUI automation, direct SQLite write, or managed-library
filesystem write is permitted. Complete fresh read-only scans revalidate the
plan before every mutation and semantically verify each command. Confirmation
is bound to the canonical library root and operation graph. An application-local
recovery guard precedes the first mutation marker, and reconciliation requires
the terminal journal and immutable summary to agree. Plans with cover-bearing
records fail closed because V1 does not model cover bytes. Milestone 7 does not
implement rollback or resume.

## Verified recovery boundary

Milestone 8 adds a separate recovery aggregate and never rewrites the source
cleanup plan, execution journal, terminal summary, or original backup.
Domain owns reconciliation classifications, semantic recovery identities,
immutable recovery plans, dependency graphs, approval/revocation, execution
lifecycle, record-ID mappings, and final-verification invariants. Application
owns three-way reconciliation, plan generation, staleness, the two-backup gate,
constructive-before-destructive ordering, safe-boundary cancellation, and
semantic verification.

Infrastructure strictly reads and rehashes Milestone 7 bundles, persists
versioned recovery plans, creates and independently verifies a new current-state
backup, stores hash-chained recovery journals and append-only history, and maps
typed recovery requests onto the existing direct `calibredb` runner. Cleanup
and recovery share one canonical library-mutation lease key. WPF owns
single-execution selection, review, individual warning acknowledgement,
approval, backup selection, destructive-phase confirmation, progress, safe-stop
requests, ID-mapping display, and accurate terminal outcomes.

Original and current-state backup generations must both verify before the first
recovery mutation. Constructive commands are verified before the separately
confirmed destructive phase; every command is followed by a complete fresh
read-only scan. Changed Calibre numeric IDs are accepted only through a unique
semantic scan delta and durable mapping. Unexpected current content is retained
and backed up, restored into a separate record where safe, or blocks automation.
The exact 9.11.0 recovery capability profile remains disabled by default until
its opt-in disposable-library qualification passes per capability.

## Errors

Distinguish validation failures, read failures, missing-file findings, malformed-format findings, operation conflicts, process failures, verification failures, and unexpected faults.

## Security

Validate paths, safely escape process arguments, avoid arbitrary commands, keep content out of logs by default, and require explicit consent before sending content to external AI services.
