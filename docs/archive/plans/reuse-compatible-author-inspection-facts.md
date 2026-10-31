# Reuse compatible author inspection facts

## Objective

Eliminate repeated author-metadata opens and repeated unchanged opening-page work
across current final-review stages, with measurable cold/warm parser counts.

## Scope

Reuse already compatible assessment metadata and workspace observations; deduplicate
metadata cache/parser demand by complete fingerprint/format/extractor/limits key.
Retain the selected-only repair and no-eager-quarantine-inspection improvements.

## Out of scope

Removing mutation copy/read-back checks, assuming mutable files unchanged,
global mutable memoization, caching raw prose, new provider/model algorithms,
rewriting all parser architecture or claiming one physical OS open across every
Calibre/internal parser operation.

## Relevant requirements

- Current user request: speed and reused facts are essential for 17,000+ books.
- [ADR 0021](../../adr/0021-matching-first-performance-oriented-product.md): complete
  version identity and separate cold/warm measurements.
- [Architecture](../../architecture.md): reuse neutral persisted evidence across stages.
- [Safety](../../safety-and-rollback.md): loss/corruption is a miss, never fake evidence.

## Existing implementation inspected

Final-author preparation already consumes compatible EPUB/PDF assessment metadata.
Its inspection orchestration can nevertheless reopen that same metadata whenever
identity remains unresolved, even if the remaining need is front-matter evidence.
Embedded metadata lacks the front-matter pass's per-key in-run deduplication and
neither pass seeds known current workspace evidence before checking disk/inspectors.
Repeated Unavailable observations are especially costly within one workflow pass.

## Proposed design

Share existing assessment compatibility checks rather than invent a new evidence
conversion or stamp observations with incorrect extractor provenance. Do not demand
embedded metadata already covered by a compatible current assessment. Reuse exact
current workspace observation keys before disk; then reuse per-run keys before
another cache read/parse. Preserve negative/incomplete coverage honestly and existing
fresh-workspace retry behavior for unavailable disk entries. Explicit selected repair
may retry unavailable local evidence, without escalating to all books.

Keep all merged observations through subsequent stages, keyed by complete versions.
No new cache/version bump and no HTTP re-fetch. Changed fingerprint or resource/
extractor version must miss. Mutations still verify actual selected copy/read-back.

## Files expected to change

Final-author compatibility helpers, the two inspection use cases, focused generated
scale/invalidation tests and current docs. The fixed naming worker also reuses its
already verified post-update format read-back instead of hashing the files a third
time; before/after verification remains mandatory.

## Safety considerations

Do not treat incomplete facets or stale assessment versions as complete metadata.
Do not convert unavailable evidence to missing/success. Preserve credit order,
provenance and independent matching/qualification/mutation authority.

## Implementation steps

1. Reproduce duplicate metadata demand with generated current-assessment and
   repeated-fingerprint fixtures.
2. Share compatibility and add complete-key in-run/workspace reuse.
3. Measure 17,000-record cold/warm/repeated-stage counters; test invalidation.
4. Run normal build/full regression checks and reconcile actual limitations.

## Tests

Compatible complete metadata produces no source open/parser start; stale/incomplete
metadata still inspects. Many associations of one fingerprint parse once even with
cache write loss. Warm workspace uses no disk-cache/source reads. Fingerprint,
format, extractor and resource-limit changes do not reuse incompatible evidence.
Selected inspection remains selected-only and keeps unselected observations.

## Verification commands

Focused Application inspections/performance tests with printed counters, then
normal Debug build, full solution, format/whitespace and dependency checks.

## Risks

Assessment facts and dedicated metadata extraction have distinct provenance; use
the existing assessment evidence path, not invented cache-origin versions. This
slice does not yet consolidate every cold assessment/signature/front-matter parser
into one physical open; do not claim that broader target is complete.

## Unresolved questions

None for this slice. Broader cold-parser consolidation requires a separate measured
change preserving all existing resource/coverage and matching policies.

## Progress

- [x] Traced the repeated demand and existing assessment evidence path.
- [x] Reproduced 17,000 parser/cache calls on both cold and warm passes when cache
  writes were unavailable; the cold pass took 69,876 ms and warm 76,702 ms.
- [x] Shared assessment compatibility now reuses intrinsic content facts across
  verified naming changes, while rejecting stale versions/fingerprints/facets.
- [x] Indexed four per-book evidence scans and added complete-key per-run/workspace
  reuse with compact merged observations. No new cache/provider version bump.
- [x] Generated 17,000-record measurement: no prior assessment => cold 20 parsers/
  20 cache reads in 913 ms, warm zero parsers/reads in 354 ms; prior assessment =>
  zero parsers/reads, 695 ms initial and 296 ms repeated review.
- [x] Focused compatibility/invalidation tests and Application measurement passed.
- [x] Removed the naming worker's redundant third full-format read/hash. Its new
  regression failed before the change; all 12 grouped Python checks and actual
  Calibre naming/quarantine acceptance passed afterward.
- [x] Normal Debug build and full solution verification completed on 2026-10-29:
  Domain 517, Application 357, Infrastructure 391, Architecture 34, WPF 108;
  all 1,407 passed, zero skipped. Formatting, whitespace and dependency audit passed.
- [x] Reconciled current documentation and archived the measured slice.

## Final outcome

Completed and verified for this bounded slice. The normal F5 build contains the
fact reuse, indexed evidence lookup and redundant-hash removal.
These are fake-adapter orchestration measurements, including deliberately failed
persistent-cache writes. They are not timing claims for actual EPUB/PDF parsing or
the personal library.

Focused scan recipe checklist across the three changed hot-path use cases:

| Recipe | Hits |
| --- | ---: |
| Sync-over-async `.Result`/`.Wait` | 0 |
| Per-call HttpClient | 0 |
| Per-call JsonSerializerOptions | 0 |
| FileStream allocation | 0 |
| stackalloc | 0 |
| Unsealed instance classes | 0 |
| Sealed instance classes | 2 of 2 |
| LINQ select/filter/order/group/lookup sites | 36 |
| Dictionary sites | 6 |
| Substring | 0 |
| Current-culture casing | 0 |
| Regex creation/source-gen sites | 0 |
| Task.Run | 0 |

The actionable issues were repeated full collection scans and repeated integration
demand, not general LINQ/string API use. No unrelated style/abstraction changes.
The broader target of one physical cold-open bundle across every assessment,
signature and front-matter feature remains separate work; do not claim it is
finished by this slice. Fresh mutation verification remains mandatory.
