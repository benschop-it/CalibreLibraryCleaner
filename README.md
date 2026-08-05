# Calibre Library Cleaner

Calibre Library Cleaner is a Windows/.NET 10 WPF application for safe,
explainable analysis and consolidation of Calibre libraries. Development uses
Visual Studio Code and GitHub Copilot Business.

## Current status

The application can build immutable read-only analysis snapshots, hash formats,
detect exact binary and exact normalized metadata candidates, assess EPUB and
PDF quality, generate and review recommendations, and remove exact duplicates
through a constrained persistent Calibre worker.

Persisted analysis loading remains available during development because a full
large-library scan can take approximately twenty minutes. Startup lists small
state manifests; explicit Load restores the saved analysis without rescanning.

Exact-binary groups can be cleaned independently of metadata candidates. Review
or override generated keeper rows, select Remove duplicates, and confirm that a
complete external library backup exists. The application does not create or
verify backups. It uses one persistent `calibre-debug` worker, logs failures,
and requires Rescan after a failed or ambiguous mutation.

The next planned roadmap milestone is Milestone 10 content fingerprints and
comparisons, after completion of the outstanding Milestone 9 manual WPF
acceptance.

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
