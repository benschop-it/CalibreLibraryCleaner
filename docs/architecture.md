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

## Persistent library-state boundary

Application owns the authoritative library-state session and persistence ports. Infrastructure owns the versioned baseline/checkpoint representation, append-only hash-chained delta journal, atomic state manifest, replay, compaction, and current-generation pruning under the user's local application-data directory. State artifacts are never written inside a Calibre library. Startup listing reads only small manifests; it never materializes a baseline, checkpoint, or journal.

One cleanup-run marker records the generation, starting revision, run identity, and total operation count before the first worker chunk. A complete successful chunk appends every typed logical delta in one write-through journal write and advances the manifest once. A failed or ambiguous chunk is not partially projected and leaves state uncertain. The marker clears only with the final successful chunk. Exact-cleanup batches defer state publication for the worker lifetime and compact one final checkpoint after successful completion.

One explicit successful scan creates a new authoritative generation. Successful typed cleanup chunks durably append and apply deterministic deltas; they never trigger catalog reads, hashing, ebook inspection, or targeted state scans. Replayed authoritative state remains mutation-eligible across restart until the developer explicitly rescans. Existing development `.library-snapshot.json` files migrate on explicit Load and are removed only after state publication succeeds.

The application intentionally does not detect external library changes between explicit scans. Failed, ambiguous, interrupted, unpersistable, or unprojectable mutations mark the generation uncertain and block all mutation until explicit rescan.

## EPUB inspection boundary

Application owns the provider-neutral `IEpubInspector` contract, inspection limits/results, deterministic scoring engine, and bounded orchestration. Infrastructure alone owns ZIP, XML, HTML, image-header, filesystem, and VersOne/Html Agility Pack types. EPUB files are preflighted and opened read-only, content is never extracted or fetched, expected untrusted-input failures become structured inspection problems, and the final snapshot is published only after all assessments complete.

## PDF inspection boundary

Application owns the provider-neutral `IPdfInspector` contract, deterministic page sampling, classification, scoring, progress, cancellation, and bounded library orchestration. Infrastructure alone owns PdfPig 0.1.15, PDF tokens/filters, read-only file handles, SHA-256 revalidation, the versioned JSON protocol, and worker-process containment. WPF deploys a fixed sibling worker executable; paths are protocol data, never process arguments. Each file gets a fresh worker with a cleared environment, managed-heap limit, Windows Job Object where available, and parent wall-time, CPU, and working-set watchdogs.

The worker opens one seekable read-only stream, reports the page header, accepts the Application-selected bounded sample, and returns provider-neutral aggregates. It does not render, OCR, execute actions, follow links, access network resources, or extract attachments. Encoded streams are checked before decode; decoded and aggregate outputs are checked immediately afterward, while process containment covers decoder allocations made before control returns. Parser failures become closed problem codes without paths, text, binary content, or raw exceptions.

`FormatAssessment` is the shared result/identity core. `EpubAssessment` and `PdfAssessment` add format-specific feature semantics. PDF classification is separately versioned and never feeds the score. PDF assessments are a separate `LibrarySnapshot` collection and are not inputs to recommendations, cleanup plans, execution, or recovery.

## Recommendation and review-export boundary

Domain owns immutable recommendation selections, reasons, warnings, decision strength, qualitative confidence, review values, and invariants. Application indexes completed Milestone 2â€“4 evidence, orchestrates deterministic generation, validates overrides, evaluates staleness, and owns the external export port. Infrastructure alone owns JSON parsing/serialization and guarded file publication outside the selected library. WPF owns session review interaction and file selection; ViewModels call Application use cases and do not write files. Recommendation review artifacts contain no cleanup-plan or mutation instructions.

## Exact-cleanup boundary

WPF owns generated keeper presentation, explicit keeper overrides, per-run external-backup confirmation, progress, and terminal status. Application builds one deterministic operation sequence, acquires the library mutation lease, and orchestrates one persistent worker. Infrastructure owns trusted Calibre discovery, the fixed embedded worker script, strict bounded JSON-lines protocol, process lifecycle, and lease storage.

The worker uses Calibre's documented database `Cache` API through one `calibre-debug` process. It transfers fingerprint-verified complementary formats only to unambiguous non-conflicting targets, removes exact duplicate/source formats, and removes records that become empty. Requests contain at most 100 operations. No shell, direct SQLite write, direct managed-library filesystem mutation, arbitrary script, direct `calibredb` mutation gateway, or second mutation engine is permitted.

Every run requires explicit confirmation that a complete external library backup exists. The application does not create, inspect, or verify that backup. A startup/preflight failure logs and stops before mutation. A failed, ambiguous, interrupted, unpersistable, or unprojectable mutation logs structured technical context, marks projected state uncertain, stops without retry or continuation, and blocks later mutation until explicit Rescan.

General cleanup plans, application-created backup bundles, execution journals/history, and automated recovery are not part of the architecture. Recommendation review remains analysis-only and cannot trigger mutation.
## Errors

Distinguish validation failures, read failures, missing-file findings, malformed-format findings, operation conflicts, process failures, verification failures, and unexpected faults.

## Security

Validate paths, safely escape process arguments, avoid arbitrary commands, keep content out of logs by default, and require explicit consent before sending content to external AI services.
