# Calibre Library Cleaner

Calibre Library Cleaner is a Windows/.NET 10 WPF application for finding and
consolidating records that contain the same work and language in large Calibre
libraries.

The product assumes the user maintains a complete external library backup. Its main
priorities are matching quality, fast repeated analysis through versioned caches,
explainable keeper/Skip review, and high-throughput cleanup through supported Calibre
tooling.

## Current workflow

The production WPF composition uses the staged workflow described below. A
compatibility analysis mode remains in code for tests/migration but is not the
configured product flow.

1. **Scan** reads the catalog, resolves files, hashes formats, and shows Exact binary
   groups.
2. **Exact cleanup** keeps one byte-identical copy, transfers safe complementary
   formats, removes duplicates, and deletes empty records.
3. The first **Candidate cleanup** activation is read-only: it reconciles the
   residual library, reuses compatible assessments/signatures, runs matching, and
   shows Unified Candidate groups.
4. The second Candidate activation executes reviewed keeper/Skip choices.

Every mutation run requires confirmation of a complete external backup. Mutation
uses one fixed persistent `calibre-debug` worker. The application does not provide
rollback or recovery.

## Matching today

Current matching combines exact normalized metadata, canonical author variants,
identifiers, title/series/language/binary evidence, explicit contradictions, and
candidate-only EPUB content signatures. Candidate generation is indexed and bounded,
and final Unified groups are disjoint.

A Candidate group means **same work and language**, not necessarily the same edition,
revision, illustrations, or formatting. All groups start included; the user can
change the generated keeper or Skip a group.

## Current limitations and next work

- Exact Scan still rehashes every resolvable file.
- PDF cross-document matching is not implemented.
- Content-language detection is not calibrated.
- Online bibliographic evidence and local embeddings/models are not implemented.
- Candidate evidence is recomputed as a complete residual run rather than
  incrementally.
- Current mutation state is more detailed than the accepted minimal target.

See [the current roadmap](docs/roadmap.md) for priorities.

## Safety model

- Analysis opens `metadata.db` and managed files read-only.
- Never write directly to `metadata.db` or Calibre-managed files.
- Keep Calibre and other writers closed during cleanup.
- Confirm an external backup before every mutation run.
- Stop on failed/ambiguous mutation and Rescan before continuing.
- No direct-command fallback, rollback, or automated recovery.

## Documentation

Start with [docs/README.md](docs/README.md). It separates current authoritative
documentation from archived milestone plans and handoffs.

## Development

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
```

Automated tests use synthetic or explicitly supplied disposable libraries, never a
personal Calibre library.

## Diagnostics

Logs are written to
`%LOCALAPPDATA%\CalibreLibraryCleaner\logs\calibre-library-cleaner-*.log`.
Production defaults to aggregate Information events. Set
`CALIBRE_DIAGNOSTIC_LOGGING=1` for bounded detailed matching/inspection diagnostics.
