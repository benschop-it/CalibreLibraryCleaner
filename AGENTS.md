# Calibre Library Cleaner Agent Instructions

## Before making changes

Read [docs/README.md](docs/README.md), the relevant current core documents, and
accepted ADRs. Archived plans/handoffs are historical and non-authoritative.

For substantial work, create or update an execution plan under `docs/plans/`
following `PLANS.md`. Implement only the requested current roadmap slice.

## Product priorities

When tradeoffs conflict, optimize these product goals in this order:

1. same-work/language matching precision and recall;
2. cold/warm throughput, cache reuse, and bounded resources;
3. explainability, keeper override, and Skip;
4. simple supported Calibre mutation.

Cancellation, resume, rollback, and recovery are not design goals. Long operations
must remain responsive and visibly active. Preserve existing cancellation only where
requested or needed for resource cleanup/safe boundaries; do not add new
cancellation points by default.

## Non-negotiable safety rules

- Never write directly to Calibre's `metadata.db`.
- Open Calibre databases read-only during analysis.
- Never rename, move, overwrite, or delete Calibre-managed files during analysis.
- Every mutation run requires explicit confirmation of a complete external backup.
- Use only the fixed persistent `calibre-debug` mutation worker and supported Calibre
  APIs; do not add a direct-command, shell, SQLite, or filesystem fallback.
- Stop on failed or ambiguous mutation. Do not retry, continue, or infer a successful
  prefix; require explicit Rescan before another mutation.
- Do not add application-managed backup, undo, rollback, or recovery features.
- Keep matching/provider/model evidence separate from mutation authority.
- Do not log book content, ordinary bibliographic metadata, provider payloads,
  credentials, paths, or embeddings by default.

The current generation/delta/marker/checkpoint implementation remains until an
explicit simplification plan changes it. Do not expand it as a product feature.

## Matching and caching standards

- Treat an executable Candidate group as same work and language; editions/revisions
  may coexist in one group.
- Published groups are included unless the user selects Skip; keeper override remains
  available.
- Preserve evidence provenance, contradictions, coverage, policy/model/provider
  versions, and deterministic IDs.
- Prefer indexed/bounded candidate generation over all-pairs comparison.
- Validate matching changes against labeled calibration and holdout data; never add
  library-specific aliases.
- Version every cache by complete input identity and all algorithm/model/resource
  versions.
- Cache corruption/loss becomes a miss, never invented evidence.
- Measure cold and warm behavior separately.

## Architecture

```text
Domain <- Application <- Infrastructure
                    <- Wpf
```

- Domain contains provider-neutral immutable values and deterministic policies.
- Application owns use cases, orchestration, progress, and integration ports.
- Infrastructure owns SQLite/filesystem/process/parser/HTTP/model/cache details.
- WPF owns presentation and references Infrastructure only in `App.xaml.cs`.
- Do not place business logic in WPF code-behind.
- Do not leak integration/runtime types into Domain.

## Development workflow

1. Inspect current implementation and tests.
2. Read current docs/ADRs; ignore archived requirements unless investigating history.
3. Create/update the execution plan.
4. Implement a narrow measurable vertical slice.
5. Add matching/cache/performance/safety tests proportional to risk.
6. Run focused tests, then standard verification.
7. Review complete diff, metrics, cache invalidation, and privacy/logging.
8. Report actual results, deviations, and remaining work.

## Coding standards

- .NET 10 and latest stable C#.
- Nullable enabled; file-scoped namespaces.
- Immutable records for evidence/results where appropriate.
- Dependency injection and structured logging.
- Async APIs for I/O and long work; no sync-over-async.
- CancellationToken may remain for existing/resource-sensitive APIs but arbitrary
  cancellation is not required for every new operation.
- Stream large files, bound concurrency/memory, canonicalize paths, and validate
  external input.
- Prefer deterministic explainable algorithms; version local ML/provider evidence.
- No global mutable state or service locator.

## Testing

Use xUnit, FakeItEasy, and FluentAssertions. Never use a personal Calibre library in
automated tests.

Prioritize matching corpus quality, cache keys/invalidation, cold/warm measurements,
malformed input/resource bounds, mutation ordering/backup confirmation, progress,
and architecture. Test cancellation only where behavior remains implemented or is
needed to release resources safely.

## Standard commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Do not claim success unless the relevant commands actually succeeded.
