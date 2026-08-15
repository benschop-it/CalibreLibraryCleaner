# SHA-256 File-Identity Reuse

## Objective

Make repeated Exact scans substantially faster by reusing a previously verified
SHA-256 only when the same canonical managed path has an exactly matching stable file
observation and complete hash/cache policy provenance. Preserve a forced full-hash
mode and all existing path/reparse/change-during-read protections.

## Scope

- Add a versioned hash-cache contract keyed by canonical library-root identity plus
  expected relative path and hash policy version.
- Store verified fingerprint and file observation: length, creation time UTC,
  last-write time UTC, and attributes.
- In `StreamingSha256FormatFileHasher`, perform normal safe-path/reparse preflight and
  current observation capture before cache lookup.
- Reuse only an exact compatible cache entry whose fingerprint size equals observed
  length; corruption, absence, stale version, or any field mismatch is a miss.
- Write/replace cache entries only after a complete stable streaming hash succeeds.
- Add a per-request forced-verification flag that bypasses cache reads but refreshes
  cache after successful hashing.
- Enable ordinary cache reuse for staged Exact Scan and post-Exact targeted hashing;
  preserve existing callers through compatible request defaults.
- Report reused files/bytes separately from freshly hashed files/bytes in progress and
  aggregate scan logging.
- Implement a bounded atomic Infrastructure JSON cache outside the Calibre library,
  storing path hashes rather than raw library/relative paths.
- Measure cold, warm, partial-change, and forced-verification behavior.

## Out of scope

- Trusting identity after path/reparse validation fails.
- Reusing hashes across different canonical library roots or expected relative paths.
- Filesystem file-index/inode identity, hardlink detection, USN journal integration,
  or cross-platform device IDs in this first slice.
- Selective/random/periodic byte revalidation policy; forced verification is the
  immediate control and periodic validation is the next cache-hardening slice.
- Incremental candidate neighborhoods, assessment/signature invalidation, or a common
  cache framework.
- Changing duplicate/matching/group/keeper policy or the reviewed matching baseline.
- Persisting cache data inside the Calibre library or modifying managed files.

## Relevant requirements

- Caches are first-class and include stable input identity plus all relevant policy
  versions.
- Canonical path, size, timestamps, attributes/file identity, and provenance must
  agree before SHA-256 reuse.
- Corruption/incompatibility is a miss, never evidence.
- A user-requested verification scan can force full hashing.
- Long operations expose truthful fresh/reused units and elapsed work.
- Analysis remains read-only; no direct SQLite or managed-file mutation.

## Existing implementation inspected

- `ScanLibraryUseCase` resolves canonical contained paths and sends all formats to
  `IFormatFileHasher`; staged `ExactOnly` does not load a previous snapshot.
- `RefreshAfterExactCleanupUseCase` also uses the hasher for targeted transfer
  verification.
- `StreamingSha256FormatFileHasher` captures length/creation/last-write/attributes,
  rejects directories/reparse points, and verifies safe path/state before open,
  before read, and after read.
- Successful results already carry `FormatFileFingerprint` and
  `FormatFileObservation`; these are persisted in snapshots but are not available to
  staged fresh scans before hashing.
- Existing content/assessment caches use external physical directories, bounded JSON,
  atomic replacement, corruption-as-miss, and pruning.
- `LibraryAnalysisOptions` configures hash concurrency but has no forced-verification
  mode.

## Proposed design

### Cache identity and values

Application contracts:

- `FormatHashCacheKey` hashes hash-policy version, canonical library root, and expected
  relative path into a 64-character key. Raw paths remain transient.
- `FormatHashCacheEntry` stores key, hash policy version, `FormatFileFingerprint`,
  `FormatFileObservation`, and `VerifiedAtUtc`.
- `IFormatHashCache.TryReadAsync`, `WriteAsync`, and `PruneAsync`.

Current hash policy: `format-sha256/1.0.0`; cache schema:
`format-hash-cache/1.0`.

The cache file name is the key hash. JSON contains no library root or relative path,
only key/version/fingerprint/observation/verification time. Default bounds: 16 KiB
per entry, 256 MiB total.

### Hasher flow

Extend `FormatHashRequest` with `ForceVerification = false`.

For each request, existing preflight first validates managed path and captures stable
state. If not forced, compute key and read cache. Reuse only if:

- key/policy match;
- cached observation equals current observation exactly after UTC normalization;
- cached fingerprint size equals current length; and
- digest is valid by Domain construction.

A hit returns normal `Success` plus `WasReused = true`; no file stream is opened.
Misses follow the unchanged streaming hash path and changed-during checks, then write
one cache entry with `WasReused = false`. Cache read/write/prune failures are ignored
as performance failures. Cancellation remains observable.

Cache access must not occur for unsafe/missing/inaccessible paths. Cache hits count
current file length as reused bytes, not freshly read bytes.

### Scan controls and reporting

Add `forceHashVerification` to `ScanLibraryUseCase.ExecuteAsync` as a compatible
optional parameter and map it into requests. A later UI affordance can expose a
separate Verify scan command; this slice provides and tests the application control
without redesigning WPF command layout.

Extend `FormatHashProgress` and Exact scan aggregate logging with reused files/bytes
and freshly hashed files/bytes. Existing total progress remains truthful and reaches
100% for both paths.

### Cache lifecycle

Infrastructure registers one singleton cache. Hasher writes successful misses and
prunes once per batch. Old/corrupt/unreadable/version-mismatched entries are misses.
An old application/cache requires one cold scan before warm reuse.

## Files expected to change

- Application hash cache/request/result/progress contracts and scan control
- Infrastructure streaming hasher, file cache/options, and DI
- Domain/Application/Infrastructure tests for identity, reuse, safety, progress,
  persistence privacy, cold/warm/partial/forced behavior
- WPF progress text if required for truthful reuse display
- `docs/architecture.md`, `docs/functional-requirements.md`,
  `docs/test-strategy.md`, `docs/roadmap.md`
- this plan and `docs/plans/README.md`

No matching policy/version, corpus, baseline, or mutation worker file should change.

## Safety considerations

- Reuse happens only after current physical safe-path preflight.
- Any observation mismatch rehashes; missing/corrupt cache never fails analysis.
- Forced verification always reads bytes and refreshes cache.
- Existing changed-before-open/during-read checks remain unchanged on misses/forced
  runs.
- Cache paths are outside the library and reject reparse points.
- Raw library/relative paths are neither filenames nor JSON values.
- Logs expose aggregate counts/bytes only.

## Implementation steps

1. Add cache key/entry/interface and request/result reuse flags with unit tests.
2. Add bounded atomic file cache and privacy/corruption/version/pruning tests.
3. Integrate cache lookup/write into hasher after preflight and test cold/warm/
   changed/forced/unsafe behavior.
4. Wire scan force flag and truthful progress/log metrics.
5. Add repeated-scan integration tests and measure cold/warm bytes/work.
6. Reconcile docs and complete standard verification.
7. Archive this plan and select periodic/selective revalidation as the next cache
   hardening slice.

## Tests

- cache key deterministic and differs by root/path/policy;
- cache JSON contains no raw paths and rejects stale/corrupt/version mismatch;
- first scan hashes and writes; second unchanged scan reuses without opening stream;
- changed length/creation/last-write/attributes causes full rehash;
- different root or relative path never reuses;
- forced verification bypasses a valid hit and refreshes it;
- missing/inaccessible/reparse/unsafe files never reuse;
- cache read/write/prune exceptions do not fail hashing;
- changed-during-read behavior remains unchanged;
- mixed batch reports exact reused/fresh file and byte totals deterministically;
- cancellation, concurrency bounds, sequence validation, and input order determinism;
- repeat scan integration proves zero fresh bytes for unchanged formats;
- no matching baseline semantic drift and no mutation boundary change.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --no-build
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --no-build
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

## Risks

- Content can theoretically change while size/timestamps/attributes are restored.
  Forced verification is included now; periodic/selective byte validation remains a
  required follow-up before treating reuse as mature.
- Filesystems expose different timestamp precision. Exact stored/current equality is
  conservative and may reduce hits, not create false hits.
- Cache cardinality can grow with moved/renamed files. Bounded oldest-entry pruning
  limits disk use.
- Hash cache may duplicate observation/fingerprint data from snapshots. This is
  intentional because staged fresh scans do not load snapshots and cache loss must
  remain only a performance event.

## Unresolved questions

- Choose periodic/selective revalidation cadence after measuring actual warm reuse and
  stale-identity risk; do not guess it in this slice.
- Decide whether the user-facing Verify control belongs beside Scan or in settings
  after the application-level forced mode is proven.

## Progress

- [x] Current staged hash flow and persistence/cache patterns inspected.
- [x] Dedicated cache ownership and forced-verification boundary selected.
- [x] Cache contracts and key tests implemented.
- [x] File cache implemented and verified.
- [x] Streaming hasher reuse integrated.
- [x] Scan/progress integration measured.
- [x] Documentation and standard verification completed.

## Final outcome

Implemented `format-sha256/1.0.0` identity reuse through Application cache/key
ports and Infrastructure-owned canonical key hashing plus bounded atomic JSON
persistence. The warm path retains containment/reparse checks and two exact stable
file observations but does not open the managed file stream. Forced verification
bypasses reads and refreshes successful entries; cache failures rehash.

Progress and aggregate logs separate fresh/reused files and bytes. A deterministic
Exact Scan integration test proved five fresh bytes cold, zero fresh/five reused
bytes warm, and five fresh bytes when forced. This is a work-reduction assertion,
not a wall-clock promise.

The full solution built and all 701 tests passed. Formatting, whitespace checks, IDE
diagnostics, and the transitive vulnerability audit were clean. Key hashing moved
behind an Infrastructure factory and mutable cache files moved outside the managed
file hashing namespace to preserve architecture/safety boundaries discovered by the
full suite.

Periodic/selective byte validation and a user-facing Verify command remain the next
slice. Matching policies, corpus, baseline, and mutation behavior did not change.
