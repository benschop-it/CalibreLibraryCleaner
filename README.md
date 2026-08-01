# Calibre Library Cleaner

Calibre Library Cleaner is a Windows/.NET 10 WPF application for safe,
explainable analysis and consolidation of Calibre libraries. Development uses
Visual Studio Code and GitHub Copilot Business.

## Current status

Milestones 0 through 9 are implemented. The application can build immutable
read-only snapshots, hash formats, detect exact binary and exact normalized
metadata candidates, assess EPUB and PDF quality, generate and review
recommendations and cleanup plans, execute narrowly supported plans through
Calibre tooling, and produce verified recovery plans.

Mutation and recovery capabilities remain fail-closed unless their exact
Calibre profile has passed the caller-gated disposable-library qualification.
Milestone 9 PDF assessment is analysis-only and does not affect retained-format
selection, cleanup, execution, or recovery.

Exact-binary groups can be cleaned independently of metadata candidates: choose
the keeper, check duplicate book records to delete, approve the plan, choose an
external backup folder, prepare, and execute. The application backs up complete
involved records and uses typed non-permanent Calibre record removal only for
the checked IDs.

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
