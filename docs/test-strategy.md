# Test Strategy

Use xUnit, FakeItEasy, and FluentAssertions. Automated tests are deterministic,
offline by default, and never use a personal Calibre library.

## Priority order

1. matching quality and deterministic evidence fusion;
2. cache correctness, invalidation, and cold/warm performance;
3. cleanup planning/worker ordering and backup confirmation;
4. parser/path/process safety;
5. progress visibility and architecture boundaries;
6. optional best-effort cancellation behavior where retained.

## Matching evaluation

Unit tests remain necessary but are not sufficient. Maintain labeled corpora with
positive same-work/language pairs, hard negatives, editions/revisions, translations,
author variants, identifier conflicts, metadata damage, format-only records, and
content/cover/PDF variants.

For each policy/model version, record:

- pair and group precision/recall on train/calibration/holdout splits;
- false-positive/false-negative categories;
- candidate-generation recall before expensive evidence;
- cap/global-limit effects;
- evidence/contradiction contribution and calibration;
- group disjointness and deterministic IDs; and
- keeper outcomes and skipped physical-state conflicts.

Do not tune against the developer's library alone or add library-specific aliases.
Online/provider and local-model tests use recorded/generated fixtures, not live
network/model downloads in the ordinary suite.

The committed `matching-corpus/1.0` resources run through the real public Domain
policies and are bound to a reviewed `matching-evaluation/1.0` semantic baseline.
Tests enforce strict bounded parsing, opaque validation errors, calibration/holdout
leakage rules, order-independent canonical digests/reports, candidate-cap metrics,
failure categories, keeper sets, and no Domain.Tests reference to Application or
Infrastructure. Runtime/allocation observations are emitted but are not golden
equality gates. External corpora are opt-in through `CALIBRE_MATCHING_CORPUS_PATH`
and `CALIBRE_RUN_EXTERNAL_MATCHING_EVALUATION=1`; they report only aggregates and
opaque IDs.

## Cache and performance tests

Every cache test covers:

- canonical input identity;
- complete algorithm/model/provider/resource version keys;
- hit, miss, corruption, incompatible version, and atomic replacement;
- dependency invalidation after changed files/metadata/policies;
- no prose/path leakage where prohibited; and
- loss/corruption degrading to recomputation rather than false evidence.

Hash-cache tests additionally cover unchanged identity reuse, selective byte
revalidation, timestamp/size/attribute changes, replacement at the same path, and
forced full verification.

Performance baselines use deterministic synthetic libraries and explicitly supplied
disposable copies. Record cold/warm durations, cache hit rates, bytes read, parser
work, candidate counts, memory, log volume, and mutation throughput. Treat observed
numbers as regression baselines, not universal time promises.

## Test levels

### Domain

Normalization, candidate scoring, evidence/contradictions, clustering, language
partition, confidence, quality scoring, keeper ranking, operation simulation, IDs,
and invariants. Domain tests contain no integration types.

### Application

Mode orchestration, demand planning, cache partitioning, progress mapping, bounded
concurrency, fallback behavior, state transitions, planning/execution ordering, and
provider/model error handling.

### Infrastructure

Read-only SQLite, path containment/reparse behavior, streaming hashing, state/cache
serialization, EPUB/PDF inspection, worker protocols/process containment, Calibre
worker integration, online-provider clients, and local-model adapters.

### WPF

Workflow gating, progress/heartbeat, evidence display, filtering/sorting, keeper
changes, Skip, backup confirmation, viewer launch, terminal outcomes, and large-group
presentation.

### Architecture

Dependency direction, no direct mutation path, parser/process types confined to
Infrastructure, WPF composition-root exception only, and no removed recovery/general
cleanup-plan systems returning.

## Fixtures

Use generated Calibre-style libraries and ebooks for:

- exact files and same-work metadata variants;
- different languages and translations;
- different editions/revisions/illustrations;
- missing, invalid, inaccessible, malformed, encrypted, and resource-heavy formats;
- EPUB package/navigation/content variants;
- digital, scan-like, mixed, encrypted, malformed, and resource-heavy PDFs;
- identifiers, series/index, author expansion, edition markers, covers, and provider
  responses; and
- cache warm/cold/invalidation scenarios.

Public-domain data may support labeled matching calibration when licensing and
provenance are recorded. No copyrighted corpus is committed without permission.

## Mutation assertions

Tests prove:

- explicit external-backup acknowledgement;
- one fixed worker and no fallback;
- chunks of at most 100 operations;
- transfers before source removals and record removals last;
- keeper same-format content wins;
- records are removed only when empty;
- stale/non-physical groups skip before mutation;
- failed/ambiguous mutation stops and requires Rescan; and
- no direct SQLite or managed-file mutation exists.

Detailed projected-delta/journal tests remain while that implementation exists. Do
not expand them as a product feature; replace them with minimal run-state tests when
the simplification is implemented.

## Progress and cancellation

Test that long operations publish truthful phase/unit/elapsed feedback and do not
look hung. Progress callbacks must be bounded and must not leak book metadata.

Cancellation tests are required only where cancellation remains implemented or is
needed to release processes/resources safely. Do not add new cancellation points for
new features by default. There is no blanket requirement for arbitrary cancellation,
restart, or partial-analysis resume.

## Online and model evidence

Provider tests cover bounded requests, field disclosure, provenance, rate limits,
cache identity, malformed responses, network failure fallback, and secret/payload
redaction. Model tests cover deterministic preprocessing, model/version identity,
bounded batches/resources, cache invalidation, and reproducible fixture outputs.

Neither provider nor model evidence can call a mutation boundary.

## Standard verification

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```
