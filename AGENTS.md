# Calibre Library Cleaner Agent Instructions

## Before making changes

Read the relevant documents under `docs/`, especially:

- `docs/product-vision.md`
- `docs/functional-requirements.md`
- `docs/architecture.md`
- `docs/domain-model.md`
- `docs/roadmap.md`
- accepted ADRs under `docs/adr/`

Treat these documents as authoritative. For substantial work, create or update an execution plan following `PLANS.md` before editing code.

## Non-negotiable safety rules

- Never write directly to Calibre's `metadata.db`.
- Open Calibre databases read-only during analysis.
- Never rename, move, overwrite, or delete Calibre-managed files during analysis.
- Never delete or replace a unique format without a verified backup.
- Destructive actions require an immutable, explicitly approved cleanup plan.
- Revalidate cleanup plans immediately before execution.
- Prefer supported Calibre tooling for library mutations.
- Verify every applied change and preserve rollback information.
- AI recommendations must never directly trigger destructive actions.

## Architecture

```text
Domain <- Application <- Infrastructure
                    <- Wpf
```

- Domain depends on no other solution project.
- Application depends only on Domain.
- Infrastructure implements interfaces declared by Application.
- WPF depends on Application and may reference Infrastructure only in the composition root.
- Do not place business logic in WPF code-behind.
- Do not leak SQLite, EPUB, PDF, filesystem, process, or WPF types into Domain.

## Development workflow

1. Inspect the existing implementation and tests.
2. Read the relevant documentation and ADRs.
3. Present or update an execution plan.
4. Implement only the requested milestone or vertical slice.
5. Add or update tests.
6. Run formatting, build, and relevant tests.
7. Review the complete diff.
8. Report assumptions, failures, risks, and remaining work.

Do not implement future roadmap items unless explicitly requested.

## Coding standards

- .NET 10 and latest stable C#.
- Nullable reference types enabled.
- File-scoped namespaces.
- Immutable records for values and analysis results where appropriate.
- Dependency injection and structured logging.
- Async APIs with propagated `CancellationToken`.
- No sync-over-async.
- Stream large files and bound concurrency.
- Validate external input and canonicalize paths.
- Prefer deterministic, explainable algorithms.
- No global mutable state or service locator pattern.
- Do not log book content by default.

## Testing

Use xUnit, FakeItEasy, and FluentAssertions.

Tests must cover successful behavior, invalid input, cancellation, missing files, malformed ebooks, duplicate conflicts, stale plans, safety invariants, and architecture boundaries. Never depend on the user's real Calibre library.

## Standard commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Do not claim success unless the relevant commands actually succeeded.
