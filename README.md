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

Exact-binary and metadata candidate groups both use generated keeper rows that
can be overridden by selecting another member. Metadata groups can be skipped;
processing transfers generated complementary formats and removes every
non-keeper record. The application requires confirmation of a complete external
library backup, uses one persistent `calibre-debug` worker, logs failures, and
requires Rescan after a failed or ambiguous mutation.

The Expanded candidates tab is review-only: inferred groups expose language,
confidence, anchors, reason codes, and bounded content evidence but cannot enter
cleanup. Double-click a member row in any duplicate/candidate tab to inspect the
selected book in Calibre ebook viewer.

Candidate-only EPUB fingerprints and local expanded discovery are implemented.
PDF cross-document fingerprints, calibrated content-language detection, and
optional online/model enrichment remain later roadmap work.

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
