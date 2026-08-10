# Calibre Library Cleaner

Calibre Library Cleaner is a Windows/.NET 10 WPF application for safe,
explainable analysis and consolidation of Calibre libraries. Development uses
Visual Studio Code and GitHub Copilot Business.

## Current status

The application can build immutable read-only analysis snapshots, hash formats,
detect exact binary and exact normalized metadata candidates, assess EPUB and
PDF quality, generate bounded local work-language candidates with candidate-only
EPUB hash evidence, generate recommendations, and consolidate exact or metadata
duplicate groups through a constrained persistent Calibre worker.

Persisted analysis loading remains available during development because a full
large-library scan can take approximately twenty minutes. Startup lists small
state manifests; explicit Load restores the saved analysis without rescanning.
Explicit Scan still SHA-256 hashes every file, but unchanged EPUB/PDF assessments
are reused from authoritative state when fingerprints and analyzer versions match.
Repeat scans should therefore avoid most archive parsing and PDF worker startup.

Exact-binary and metadata candidate groups both use generated keeper rows that
can be overridden by selecting another member. Metadata groups can be skipped;
processing transfers generated complementary formats and removes every
non-keeper record. The application requires confirmation of a complete external
library backup, uses one persistent `calibre-debug` worker, logs failures, and
requires Rescan after a failed or ambiguous mutation.

The Expanded candidates tab presents content-confirmed same-work/same-language
groups with a generated sole keeper, Keep/Remove actions, Skip, evidence, and
explicit backup-confirmed cleanup. It does not rewrite title or author metadata.
Double-click a member row in any duplicate/candidate tab to inspect the selected
book in Calibre ebook viewer.

After reviewing keeper and Skip choices in all three tabs, use `Cleanup all` to
validate overlaps and execute one transfer/removal plan from the initial scan.
Conflicting choices are shown before backup confirmation and no mutation starts
until they are corrected.

ADR 0020 defines the in-progress replacement: exact-only analysis, the unchanged
Exact cleanup algorithm, trusted post-exact refresh, and one residual Candidate
cleanup workflow. Legacy category commands and `Cleanup all` remain available only
during migration and will be retired after staged behavioral and safety parity.

Candidate-only EPUB fingerprints and local expanded discovery are implemented.
PDF cross-document fingerprints, calibrated content-language detection, and
optional online/model enrichment remain later roadmap work.

## Diagnostics

Application and scan diagnostics are written through Serilog to
`%LOCALAPPDATA%\CalibreLibraryCleaner\logs\calibre-library-cleaner-*.log`.
Logs roll daily and at 25 MB, retain the most recent 20 files, and flush buffered
events to disk every second so completed and failed scans can be investigated
after the application closes.

## Development

1. Open the repository root in Visual Studio Code.
2. Read `AGENTS.md`, nested instruction files, and the relevant documentation.
3. Review or create an execution plan under `docs/plans/` for substantial work.
4. Implement only the approved milestone or vertical slice.
5. Run the standard verification commands before reporting completion.

## Important files

- `AGENTS.md` — repository-wide Codex instructions.
- `PLANS.md` — execution-plan requirements.
- `docs/roadmap.md` — milestone order and scope.
- `docs/architecture.md` — project boundaries.
- `docs/safety-and-rollback.md` — non-negotiable safety model.
- `docs/adr/` — accepted architectural decisions.
- nested `AGENTS.md` files — project-specific instructions.

## Standard verification

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Never use a personal Calibre library in automated tests. Use synthetic fixtures
and temporary directories. Never write directly to `metadata.db`.
