# Architecture

## Projects

```text
src/
  CalibreLibraryCleaner.Domain/
  CalibreLibraryCleaner.Application/
  CalibreLibraryCleaner.Infrastructure/
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

## EPUB inspection boundary

Application owns the provider-neutral `IEpubInspector` contract, inspection limits/results, deterministic scoring engine, and bounded orchestration. Infrastructure alone owns ZIP, XML, HTML, image-header, filesystem, and VersOne/Html Agility Pack types. EPUB files are preflighted and opened read-only, content is never extracted or fetched, expected untrusted-input failures become structured inspection problems, and the final snapshot is published only after all assessments complete.

## Recommendation and review-export boundary

Domain owns immutable recommendation selections, reasons, warnings, decision strength, qualitative confidence, review values, and invariants. Application indexes completed Milestone 2â€“4 evidence, orchestrates deterministic generation, validates overrides, evaluates staleness, and owns the external export port. Infrastructure alone owns JSON parsing/serialization and guarded file publication outside the selected library. WPF owns session review interaction and file selection; ViewModels call Application use cases and do not write files. Recommendation review artifacts contain no cleanup-plan or mutation instructions.

## Cleanup-plan boundary

Domain owns immutable cleanup-plan bodies, expected library/record/format state, declarative retention and removal intentions, backup requirements, provenance, validation issues, canonical semantic hashes, and lifecycle transition rules. Application owns explicit generation from one current reviewed recommendation, current-snapshot validation, staleness, approval, revocation, and import/export orchestration. Infrastructure alone owns `cleanup-plan/1.0` JSON parsing/serialization and guarded external file storage. WPF owns review and confirmation interaction; ViewModels do not serialize or access files.

Cleanup plans are non-executable data. Milestone 6 introduces no Calibre process, command, backup creator, lock, mutation, simulation, or rollback boundary.

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

## Errors

Distinguish validation failures, read failures, missing-file findings, malformed-format findings, operation conflicts, process failures, verification failures, and unexpected faults.

## Security

Validate paths, safely escape process arguments, avoid arbitrary commands, keep content out of logs by default, and require explicit consent before sending content to external AI services.
