# Reuse compatible metadata cache after author display fixes

## Objective

Restore cache-first metadata review after display/order policy changes without
restoring pseudonym annotations, compact initials or invented coauthor ordering.

## Scope

Known legacy proposal/query keys, local positive revalidation, compatible negative
reuse, truthful legacy author-order provenance, current-order alignment and bounded
identifier-alias backfill. Preserve freshness, request limits and current cache hits.

## Out of scope

Deleting user caches, changing provider endpoints, matching thresholds, unknown
version compatibility, unvalidated title-based cache searching, mutation-engine
changes, interrupting the user's running operation or reading a personal library.

## Relevant requirements

- [ADR 0021](../../adr/0021-matching-first-performance-oriented-product.md): warm reuse
  and complete version identity, not avoidable repeated HTTP work.
- [Architecture](../../architecture.md): reduced cache evidence, provenance and review.
- [Safety](../../safety-and-rollback.md): evidence never grants mutation authority.

## Existing implementation inspected

The author-order fix advanced proposal interpretation from `1.0.0` to `1.1.0`,
which participates in every cache filename; lookup only computes the new identity.
The compatibility list also omits query `1.6.0`. Old positive and negative results
therefore miss even when retrieval inputs and non-author facts are compatible.
Read-only diagnostics observed 82,076 cache files, legacy headers, and a running
preparation at 0 cached / 398 online requests. No bibliographic values were logged.

## Proposed design

Try current identities first, then bounded known legacy identities with exact
input/provider/version checks. Revalidate positive candidates locally using current
resolution policy. Reuse compatible neutral outcomes without changing timestamps,
status, TTL or failure codes. Promote compatible results into current cache identities.
Retain old lookup/extraction miss restrictions where retrieval semantics changed.

Legacy outer author arrays were alphabetized; mark unknown coauthor order rather
than claim it survived. Single stored credit order is intact. For unknown multi-
credit order, apply authors only if the complete prepared current credit set proves
the correspondence, retaining that current order; otherwise withhold authors while
keeping valid non-author metadata usable. Raw provider evidence remains visible.
Carry this fact through fusion/cache/promotion and version derived review choices.
Retain original provider/query/proposal versions and the opaque original query
identity through local revalidation, promotion, aliases and Application rebinding;
show source versions in WPF rather than implying a new provider retrieval.

## Files expected to change

Existing cache adapter, Domain candidate and shared preparation/application policy,
Application author/disposition consumers, WPF disclosure, focused tests and current docs.

## Safety considerations

No fabricated evidence, confidence, retrieval freshness or source credit order.
Unknown/corrupt/stale entries remain misses. Provider failures remain unavailable,
not negative matches or transfer authority. No coauthor loss or arbitrary order.

## Implementation steps

1. Freeze generated legacy-cache regressions before code changes.
2. Add exact bounded legacy lookup, local validation and timestamp-preserving promotion.
3. Propagate author-order uncertainty and safe book-aware credit preparation.
4. Rebuild compatible ISBN aliases without losing version/provenance restrictions.
5. Measure first-reuse/warm requests and validate name fixes, caches and workflows.

## Tests

Legacy current/intermediate queries; positive/no-match/ambiguous/unavailable cases;
wrong inputs, unsupported versions, corruption, expiration and extraction misses;
single versus lost multi-author order; explicit current pipe/comma alignment;
pseudonyms/initials; promotion/fusion/alias provenance; zero HTTP on compatible warm
fixtures, unchanged timestamps and existing request budgets.

## Verification commands

Focused Domain/Application/Infrastructure/WPF tests, generated file-cache cold/warm
measurement, then standard solution verification when normal outputs are unlocked.
No production library, live provider or alternate-output build-lock bypass.

## Risks

Legacy negatives may encode older extraction misses; preserve explicit compatibility
rules. Source order cannot be recovered from sorted legacy arrays; only independent
current credits can establish a safe order. One-time alias migration must be bounded
and cannot silently promote unsupported data.

## Unresolved questions

None. Known compatible facts should be reused; missing order stays explicitly unknown.

## Progress

Additional compatibility checks reproduced two coupled gaps: provider-version-only
changes bypassed local validation/extraction-miss refresh, and positively revalidated
older-provider entries did not seed ISBN aliases after the one-time marker. Both now
use the same bounded compatibility/alias path, preserve newer entries and avoid
recursive alias expansion. The added regressions failed before their fixes.

- [x] Confirmed regression in version-key construction and running aggregate counters.
- [x] Existing user cache/artifacts and running review left unchanged.
- [x] Reproduced exact historical-key misses before implementation.
- [x] Implemented bounded compatible lookup, revalidation, aliases and promotion.
- [x] Preserved source provenance, timestamps, retry age and author-order coverage.
- [x] Verified complete Domain (484), Application (325), Infrastructure (384),
  Architecture (34) and WPF (93) suites: 1,320 passed, zero skipped.
- [x] Generated 1,000-entry real-file-cache fixture reused all entries with zero
  provider requests on first legacy reuse and warm preparation.
- [x] Normal Debug application rebuilt with zero warnings/errors; full solution
  tests, format verification, whitespace check and dependency vulnerability audit
  succeeded. No alternate outputs or user-process termination.
- [x] Reconciled current documentation and archived this completed plan.

## Final outcome

Implemented exact recognized legacy reuse without a further raw proposal/query
version bump. Derived fusion policy is `edition-metadata-fusion/1.6.0`; raw proposal
and query versions remain `1.1.0` and `1.7.0`. Unknown author order, source provenance,
retrieval age and failure status remain explicit. Missing coverage cannot authorize
an author write or automatic Unresolvable transfer.

Final generated real-file-cache/fake-provider measurement:

| Measure | Result |
| --- | --- |
| Generated legacy entries | 1,000 |
| Source query/proposal versions | `1.6.0` / `1.0.0` |
| Fixture outcomes | 250 positive, 250 fresh unavailable, 500 no-match |
| First compatible legacy reuse | 6,478 ms; 1,000 cache hits |
| Warm repeat | 1,645 ms; 1,000 cache hits |
| Provider requests, both preparations | 0 |
| Current-key promotions | 1,000; old entries retained |
| Additional warm entries | 0 |
| Retrieval timestamps | Preserved |
| Peak test-process working set | 103,006,208 bytes |

This measures generated first-legacy-reuse and warm behavior, not network-cold or
production-library throughput. Genuinely changed queries, expired failures, older
extraction misses and incompatible/corrupt entries can still require bounded
provider requests. The first production preparation performs a one-time local alias
compatibility pass; no manual cache deletion or new library scan is required by this
cache fix.

An initial standard-suite run hit an unrelated PDF fixture disposal file-lock race.
Its focused retry and subsequent complete solution runs passed; no PDF behavior was
changed. The original user cache/library and review state were not altered by
diagnostics or generated verification. Normal Visual Studio F5 outputs are current.
