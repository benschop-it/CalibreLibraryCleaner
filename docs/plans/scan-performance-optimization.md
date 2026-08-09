# Scan Performance Optimization

## Objective

Reduce full-scan wall time for libraries around 27,000 records while preserving complete SHA-256 verification, bounded untrusted-file parsing, deterministic results, and cleanup safety.

## Scope

- Add structured top-level scan phase timings and reuse counts.
- Increase default EPUB assessment concurrency from two to a bounded maximum of four.
- Reuse prior authoritative EPUB/PDF assessment facts only after the current scan computes an identical SHA-256/size fingerprint and analyzer/scoring/resource versions match.
- Rebind reused immutable facts to the current record ID, relative path, fingerprint, and observation.
- Preserve fresh assessment for cache misses, changed bytes, version changes, absent prior state, and invalid associations.
- Keep content-signature cache reuse unchanged.

## Out of scope

- Skipping SHA-256 based only on timestamp/size.
- Reusing PDF worker processes across files.
- Weakening EPUB/PDF resource limits.
- Parallelizing path safety validation before profiling proves it material.
- Replacing the snapshot/state store with a separate assessment database in this slice.

## Relevant requirements

- Exact duplicate safety requires current full-file SHA-256.
- EPUB/PDF inputs remain untrusted and bounded.
- Analyzer/model/resource version changes invalidate reuse.
- No book content or paths are logged.
- Long-running phases remain cancellable and progress-reporting.

## Existing implementation inspected

- Every scan hashes every format, then reassesses every EPUB and every PDF.
- EPUB assessment defaults to two concurrent workers.
- PDF assessment starts a new isolated worker per PDF.
- Candidate content signatures already have a fingerprint/version cache.
- Current authoritative state can hold previous immutable assessments in-process or after explicit Load.
- Top-level scan currently lacks one phase-duration summary.

## Proposed design

After current hashing completes, partition EPUB/PDF targets into reusable and fresh sets. Reuse requires exact fingerprint equality and current analyzer/scoring versions; PDF additionally requires the current resource-profile version. Reconstructed assessment values retain previous bounded features/findings but use current associations and observations. Fresh targets continue through existing bounded inspectors.

Progress adapters add reused counts to fresh completion so UI totals still describe every target. Structured completion logging reports record/format counts and milliseconds for catalog/path, hashing, EPUB, PDF, matching/grouping/recommendations, and total scan, plus reused assessment counts.

Default EPUB assessment concurrency becomes `min(4, max(2, processorCount / 2))`. The existing per-file HTML/resource ceilings bound memory. PDF concurrency remains two until measured process/working-set data supports a change.

## Files expected to change

- `LibraryAnalysisOptions`.
- New Application assessment reuse policy and tests.
- `ScanLibraryUseCase` orchestration, progress, and structured timing.
- WPF composition only through existing DI.
- Performance and architecture documentation.

## Safety considerations

- Reuse never bypasses current hashing.
- A fingerprint/version mismatch is a cache miss, not a partial reuse.
- Reused assessments contain no recoverable prose.
- Prior uncertain state is never a reuse source.
- Any reconstruction invariant failure falls back to fresh assessment.

## Implementation steps

1. Add and test immutable assessment reuse partitioning.
2. Integrate reuse into scan progress and assessment orchestration.
3. Add top-level phase timing logs.
4. Raise bounded EPUB concurrency.
5. Run full build/tests/format/diff/vulnerability verification.

## Tests

- Identical fingerprint/current versions reuse EPUB/PDF assessments.
- Changed fingerprint, analyzer, scoring, or PDF resource profile forces fresh assessment.
- Rebound IDs/paths/observations are current.
- Prior uncertain or absent state yields no reuse.
- Progress totals include reused plus fresh targets.
- Scan timing logs contain only counts/durations.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

## Risks

- Reuse helps repeat scans but not the first cold scan.
- Four concurrent EPUB DOM parses increase peak memory; existing 2M-character/50k-node ceilings limit this risk.
- Prior state is available automatically only when loaded/current in the state session.

## Unresolved questions

- Whether a future dedicated assessment cache should make reuse available without explicitly loading prior authoritative state.

## Progress

- [x] Full scan pipeline and likely bottlenecks inspected.
- [x] Safe reuse/concurrency contract documented.
- [x] Assessment reuse implemented.
- [x] Phase timing implemented.
- [x] Complete validation performed.

## Final outcome

Explicit Scan now loads prior authoritative application state when needed, hashes every current format, and reuses immutable EPUB/PDF assessments only on exact fingerprint and current-version matches. Progress reports reused/fresh counts, EPUB concurrency is CPU-aware and capped at four, and one structured completion event reports phase durations and reuse totals. The complete solution builds; all 549 tests pass; formatting/diff checks pass; and the package vulnerability audit is clean.
