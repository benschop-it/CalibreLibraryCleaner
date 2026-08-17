# Calibre Library Cleaner

Calibre Library Cleaner is a Windows/.NET 10 WPF application for cleaning one messy,
disposable copy of a Calibre library before it replaces the original library. It
finds and consolidates records containing the same work and language, then is being
finished to improve retained metadata and normalize Calibre-managed names.

The product assumes the user maintains a complete external library backup. Cleanup
is performed through supported Calibre tooling; analysis never writes to the Calibre
database or managed files.

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
4. The second Candidate activation executes reviewed keeper/Skip choices through the
   persistent worker without online metadata lookup.
5. After Candidate cleanup, an explicit action resolves online metadata only for
   retained records; a later confirmed action applies checked proposals.

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

## Remaining finish line

Matching calibration is frozen for this workflow. Exact Scan can reuse SHA-256 for
unchanged stable file observations, and Open Library currently provides same-work
identity evidence only. Optional configured Ollama observations did not produce an
acceptable threshold and do not affect grouping or cleanup.

Review/operation ambiguity is removed, independent rich-edition boundaries exist for
Open Library and optional keyed Google Books, and deterministic fusion now produces
one coherent proposal per Unified group or residual singleton. Candidate preparation
and compatible restart/load now show a virtualized review surface and restore bounded
Apply overrides. Checked proposals now use the fixed worker with verified Calibre
read-back. A deterministic framework-dependent Windows package includes an isolated
complete PDF-worker publish. The remaining acceptance work is the packaged destructive
workflow on an explicitly backed-up disposable library, followed later by separately
approved Calibre-managed name normalization.

See [the current roadmap](docs/roadmap.md) for this bounded finish line.

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

## Windows release ZIP

The release targets Windows x64 and requires the .NET 10 Desktop Runtime x64 plus a
supported Calibre 9.x installation (`9.11.0` or newer, below `10.0.0`). Build and
verify the versioned ZIP with:

```powershell
.\scripts\Build-ReleasePackage.ps1 -Version 0.1.0
.\scripts\Test-ReleasePackage.ps1 -ZipPath .\bin\releases\CalibreLibraryCleaner-0.1.0-win-x64-framework-dependent.zip
```

Always begin with a disposable representative library copy. Confirm a complete
external backup immediately before each mutation run. See the
[release acceptance workflow](docs/workflows/windows-release-acceptance.md).

## Diagnostics

Logs are written to
`%LOCALAPPDATA%\CalibreLibraryCleaner\logs\calibre-library-cleaner-*.log`.
Production defaults to aggregate Information events. Set
`CALIBRE_DIAGNOSTIC_LOGGING=1` for bounded detailed matching/inspection diagnostics.
