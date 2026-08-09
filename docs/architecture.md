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

An explicit Scan always hashes every current format for exact-duplicate and mutation safety. After hashing, unchanged EPUB/PDF assessments may be rebound from prior authoritative state only when SHA-256/size and analyzer/scoring/resource versions match. Missing, uncertain, changed, or version-incompatible facts are inspected fresh. Explicit Scan may load the application-owned prior baseline to enable this reuse after restart; startup listing remains manifest-only. EPUB assessment concurrency is CPU-aware and bounded from two through four workers. A structured scan-completion event reports phase durations and reuse counts without paths or book metadata.

The runtime assumes no unrelated process mutates the selected library while the cleaner is active. Calibre mutations are initiated only by the cleaner's fixed worker. This permits future removal of duplicate race checks, but does not permit timestamp-only identity, skipped hashing, skipped path containment, or weaker parser limits.

## Persistent library-state boundary

Application owns the authoritative library-state session and persistence ports. Infrastructure owns the versioned baseline/checkpoint representation, append-only hash-chained delta journal, atomic state manifest, replay, compaction, and current-generation pruning under the user's local application-data directory. State artifacts are never written inside a Calibre library. Startup listing reads only small manifests; it never materializes a baseline, checkpoint, or journal.

One cleanup-run marker records the generation, starting revision, run identity, and total operation count before the first worker chunk. A complete successful chunk appends every typed logical delta in one write-through journal write and advances the manifest once. A failed or ambiguous chunk is not partially projected and leaves state uncertain. The marker clears only with the final successful chunk. Exact-cleanup batches defer state publication for the worker lifetime and compact one final checkpoint after successful completion.

One explicit successful scan creates a new authoritative generation. Successful typed cleanup chunks durably append and apply deterministic deltas; they never trigger catalog reads, hashing, ebook inspection, or targeted state scans. Replayed authoritative state remains mutation-eligible across restart until the developer explicitly rescans. Existing development `.library-snapshot.json` files migrate on explicit Load and are removed only after state publication succeeds.

The application intentionally does not detect external library changes between explicit scans. Failed, ambiguous, interrupted, unpersistable, or unprojectable mutations mark the generation uncertain and block all mutation until explicit rescan.

## EPUB inspection boundary

Application owns the provider-neutral `IEpubInspector` contract, inspection limits/results, deterministic scoring engine, and bounded orchestration. Infrastructure alone owns ZIP, XML, HTML, image-header, filesystem, and VersOne/Html Agility Pack types. EPUB files are preflighted and opened read-only, content is never extracted or fetched, expected untrusted-input failures become structured inspection problems, and the final snapshot is published only after all assessments complete.

Ordinary EPUB assessment rejects decoded HTML above 2,000,000 characters and documents above 50,000 HTML elements before/around DOM traversal. This protects the in-process non-cooperative HTML parser from monolithic dictionary/reference chapters that can otherwise monopolize a scan worker for minutes. Such files remain in the snapshot with incomplete coverage and a controlled `LimitExceeded` warning; they do not fail the scan. Progress reports content/fallback chapter units, and structured diagnostics report record ID, file bytes, stage units, technical counts, total time, and warnings for assessments taking at least five seconds without logging paths or content. The resource-profile change is versioned as `epub-inspector/1.0.5`.

## Expanded matching boundary

Domain owns bounded metadata profiles, canonical candidate pairs, fixed-point evidence/contradictions, hash-only EPUB signatures, symmetric comparison, constrained clustering, policy-versioned work-language groups with an advisory evidence classification, and matching run summaries. Application owns candidate-only demand planning, fingerprint/version cache ports, bounded concurrency, progress, and explicit-scan orchestration. Infrastructure reuses the safe EPUB boundary to produce transient visible-text tokens but returns only hashes/counts; its atomic disposable cache is outside the library and contains no paths or prose.

Author identity is the first ordinary-candidate boundary. Canonical aliases use normalized family name plus positional given-name initials and non-conflicting full expansions; comma-order and punctuation differences do not create separate authors. Work candidates are searched only inside compatible author identities and require independent title, identifier, series/index, or binary evidence. Author similarity alone never proposes a work.

Only non-binary work candidates with two usable EPUB targets request content, and equivalent/high-similarity content is required before final grouping. Known languages partition final work groups; complete-component author, language, series, and content contradictions block union. Weak/unavailable evidence never merges. Projected mutations never rerun matching; they discard inferred evidence and require explicit Rescan. Inferred groups never enter cleanup requests.

The signature cache validates its complete storage-root ancestor chain because any ancestor reparse point can redirect an apparently safe path. Once that controlled root is validated, immediate cache children use leaf-only checks. Cache writes are atomic but intentionally non-durable because cache loss affects only performance. Pruning runs once after a resolver batch, not after each entry.

Matching diagnostics use structured logs without paths or book metadata. Information events report pair/fingerprint demand, periodic completion, aggregate cache/inspection/write/prune time, and cache size/pruning. Debug events report each fingerprint and EPUB preflight/counting/sampling timing. The WPF host writes these events through Serilog to bounded rolling files under `%LOCALAPPDATA%\CalibreLibraryCleaner\logs`, with Debug enabled for matching and EPUB inspection categories. Composite-cleanup preflight logs submitted/user-skipped counts, Expanded policy eligibility, planned category counts, operation count, and conflict count without titles or paths. The UI status also reports fingerprint ordinal and current preflight/counting/sampling chapter progress.

## PDF inspection boundary

Application owns the provider-neutral `IPdfInspector` contract, deterministic page sampling, classification, scoring, progress, cancellation, and bounded library orchestration. Infrastructure alone owns PdfPig 0.1.15, PDF tokens/filters, read-only file handles, SHA-256 revalidation, the versioned JSON protocol, and worker-process containment. WPF deploys a fixed sibling worker executable; paths are protocol data, never process arguments. Each file gets a fresh worker with a cleared environment, managed-heap limit, Windows Job Object where available, and parent wall-time, CPU, and working-set watchdogs.

The worker opens one seekable read-only stream, reports the page header, accepts the Application-selected bounded sample, and returns provider-neutral aggregates. It does not render, OCR, execute actions, follow links, access network resources, or extract attachments. Encoded streams are checked before decode; decoded and aggregate outputs are checked immediately afterward, while process containment covers decoder allocations made before control returns. Parser failures become closed problem codes without paths, text, binary content, or raw exceptions.

`FormatAssessment` is the shared result/identity core. `EpubAssessment` and `PdfAssessment` add format-specific feature semantics. PDF classification is separately versioned and never feeds the score. PDF assessments are a separate `LibrarySnapshot` collection and are not inputs to recommendations, cleanup plans, execution, or recovery.

## Recommendation boundary

Domain owns immutable recommendation selections, reasons, warnings, decision strength, qualitative confidence, and invariants. Application indexes completed Milestone 2–4 evidence and orchestrates deterministic generation. Metadata candidate cleanup uses the generated metadata source as its default keeper and generated selected format sources for complementary transfers; user keeper/Skip choices remain transient WPF state.

## Duplicate-cleanup boundary

WPF owns generated keeper presentation for exact, metadata, and expanded groups, advisory `Cleanup eligible`/`To be reviewed` text, explicit keeper overrides and Skip choices, per-run external-backup confirmation, progress, and terminal status. Both advisory Expanded types are included unless explicitly skipped. Application builds one deterministic operation sequence, acquires the library mutation lease, and orchestrates one persistent worker. Infrastructure owns trusted Calibre discovery, the fixed embedded worker script, strict bounded JSON-lines protocol, process lifecycle, and lease storage.

`Cleanup all` is a composite Application boundary over the three selection sets. It forms connected consolidation components from overlapping Metadata and Expanded groups, chooses one global keeper with the existing quality policy unless an explicit user override applies, normalizes Exact operations around that survivor, and simulates final format inventory. Contradictory explicit overrides and true operation/state hazards block before tool discovery; generated keeper disagreement does not. Physically incomplete groups are skipped and counted. WPF formats conflict record IDs/titles for a modal; conflict logs and Application values contain no book content. The result is one canonical operation graph from the initial scan without an intermediate rescan.

The worker uses Calibre's documented database `Cache` API through one `calibre-debug` process. Exact cleanup transfers fingerprint-verified complementary formats only to unambiguous non-conflicting targets. Metadata cleanup transfers generated complementary sources to the selected keeper, removes every format from non-keepers, and removes the emptied records. When the metadata keeper already has a format, its file wins even when a removed alternative is not byte-identical. Requests contain at most 100 operations. No shell, direct SQLite write, direct managed-library filesystem mutation, arbitrary script, direct `calibredb` mutation gateway, or second mutation engine is permitted.

Every run requires explicit confirmation that a complete external library backup exists. The application does not create, inspect, or verify that backup. A startup/preflight failure logs and stops before mutation. A failed, ambiguous, interrupted, unpersistable, or unprojectable mutation logs structured technical context, marks projected state uncertain, stops without retry or continuation, and blocks later mutation until explicit Rescan.

General cleanup plans, application-created backup bundles, execution journals/history, and automated recovery are not part of the architecture.

## External ebook viewer boundary

WPF routes an explicit member-row double-click to an Application launcher port. Infrastructure resolves only the trusted `ebook-viewer.exe` sibling of the configured Calibre executable, validates the requested format as a physical regular file contained in the selected library, and starts the viewer with `UseShellExecute=false` and one argument-list item. The cleaner does not wait for, control, or infer state from the viewer process.

Exact duplicate rows open their represented format. Metadata and expanded-candidate rows choose the first present format in deterministic reading preference order: EPUB, AZW3, MOBI, PDF, then remaining formats alphabetically. Missing formats, unsafe paths, and missing/failed viewer launches return controlled errors and never affect cleanup selection or projected state.
## Errors

Distinguish validation failures, read failures, missing-file findings, malformed-format findings, operation conflicts, process failures, verification failures, and unexpected faults.

## Security

Validate paths, safely escape process arguments, avoid arbitrary commands, keep content out of logs by default, and require explicit consent before sending content to external AI services.
