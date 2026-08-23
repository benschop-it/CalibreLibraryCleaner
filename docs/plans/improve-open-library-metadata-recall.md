# Improve Open Library Metadata Recall

## Objective

Increase rich metadata proposal recall for retained books while preserving conservative
same-work identity, bounded provider traffic, complete provenance, and cache safety.
Different editions of the same work are acceptable metadata sources when exact-edition
metadata is unavailable.

## Scope

- Extract multiple bounded compatible editions from each Open Library work result.
- Add deterministic bounded fallback query variants after a strict query does not
  produce a proposal.
- Version provider/query/cache identities so old misses cannot mask improved behavior.
- Measure recall changes against the current cached Open Library outcome corpus.

## Out of scope

- Google Books activation or API-key UX changes; the existing protected key dialog is
  retained for a later independent provider step.
- Scraping Google Search, Goodreads, Amazon, or other HTML pages.
- Metadata mutation policy changes or automatic application of Low-confidence results.
- Matching/grouping authority changes.

## Relevant requirements

- Rich metadata may come from a different edition of the same work.
- Provider calls remain bounded, cache-first, user-triggered, and provenance-preserving.
- Broader fallback matches remain Low unless exact title/author, strong identifier, or
  independent provider evidence justifies higher confidence.

## Existing implementation inspected

- Open Library currently submits one ISBN query or title + all authors + optional
  language query.
- It reads at most five works but only the first nested edition from each work.
- The current cache contains 16,711 `OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS`, 3,374
  NotFound, 334 Ambiguous, and 4,792 Proposed outcomes.
- Google Books was not active in the measured Library 6 review run.

## Proposed design

1. Parse several nested editions per returned work, bounded globally to five candidates.
2. Rank candidate editions for field richness while retaining work and edition IDs.
3. Execute fallback variants only after stricter variants produce no proposal:
   - title + all authors + language;
   - title + all authors without language;
   - title + primary author;
   - canonical title without subtitle/edition markers + primary author.
4. Cache every variant by complete normalized identity and policy/provider version.
5. Fuse results through the existing proposal policy; fallback provenance lowers
   confidence unless exact matching evidence supports it.

## Files expected to change

- Domain edition query/proposal contracts and policy tests.
- Application proposal resolver and cache identity tests.
- Infrastructure Open Library adapter/options/tests.
- Functional requirements and architecture documentation.

## Safety considerations

- Analysis remains read-only.
- No direct Calibre database or file mutation.
- Request count, response bytes, result counts, and candidate fields remain bounded.
- Broader retrieval never bypasses deterministic proposal validation.

## Implementation steps

1. Add multi-edition extraction with bounded ranking.
2. Add query variants and resolver fallback orchestration.
3. Version provider/query/cache identities.
4. Add malformed response, request-bound, provenance, and confidence tests.
5. Replay a measured sample/current corpus and report strict versus fallback gains.
6. Run standard verification and package smoke.

## Tests

- Multiple editions where the first is unusable and a later edition is valid.
- Candidate bound across multiple works/editions.
- Strict query success prevents fallback requests.
- Language fallback, primary-author fallback, and canonical-title fallback order.
- Cache identity/version invalidation.
- Ambiguous/broad matches remain unchecked Low or Unavailable.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

## Risks

- Broader queries can retrieve another work with similar title/author text.
- Extra variants can substantially increase provider requests.
- Open Library nested edition ordering and completeness are inconsistent.

## Unresolved questions

- Whether canonical-title fallback should strip subtitles only or also recognized
  edition/language markers.
- Whether a work editions endpoint is needed after multi-edition nested extraction is
  measured.

## Progress

- [x] Current provider, query, cache, and outcome corpus inspected.
- [x] Multi-edition extraction implemented.
- [x] Bounded fallback variants implemented.
- [x] Budget-aware work editions endpoint fallback implemented.
- [x] Bounded recall replay measured.
- [x] Standard verification passed after work editions fallback: 829 tests,
  formatting, diagnostics, diff check, and vulnerability audit.

The first v1.1 replay processed 643 books with 1,923 requests and produced zero new
proposals. Diagnosis proved text fallbacks were still validated through the original
ISBN branch, so candidates from another edition were rejected before title/author
comparison. Query policy `1.2.0` now validates only the strict ISBN request by ISBN;
text fallbacks are validated against the original full title, all authors, and optional
language. A regression with a different candidate ISBN now produces exact title/author
evidence. The 1,829 false v1.1 cache entries are invalidated by the policy version.

The corrected v1.2 request-limited replay wrote 10,000 variant outcomes: 418 Proposed,
97 Ambiguous, 9,397 NotFound, and 88 timeout/unavailable. It completed 6,012 of 17,283
query groups, so 11,271 groups were actually deferred. The old UI mixed variant HTTP
request/cache-hit counts with query-group counts and displayed 4,495 deferred while
leaving the terminal progress bar near 35%. Provider batches now expose explicit
completed query groups; workspace deferred counts use that value, and terminal review
readiness sets the progress bar to 100% while retaining deferred coverage in text.

Selective legacy-cache reuse and conservative core-title matching followed in query
policies 1.3 and 1.4. The bounded v1.4 diagnostic still converted poorly: 362 of 392
new outcomes (92.3%) were `OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS`, while two proposals
included a valid different-edition match for `Quicksilver`. Official Search API
documentation confirms that only one nested edition is currently returned. Provider
version `open-library-edition-search/1.2.0` therefore uses a second request only when
search returns no usable nested edition and the remaining run budget permits it. The
request targets the supported `/works/{id}/editions.json?limit=5` endpoint for the
first title/author-compatible work, parses at most five editions, and reports both
HTTP requests to Application's hard limit.

The packaged provider `1.2.0` replay was stopped after a decisive bounded sample.
Its frozen terminal state had 5,207 cached outcomes: 66 Proposed (1.27%), eight
Ambiguous, and 4,599 `OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS` (88.32%). Provider `1.1.0`
under the same query policy had produced two Proposed outcomes from 665 (0.30%) and
614 no-compatible outcomes (92.33%). All 66 proposals had valid `/books/OL...M`
provenance and at least one rich field. The detailed 65-proposal quality snapshot
contained 64 publishers, 57 identifiers, and 41 languages. The new result rate is
therefore about 4.2 times the prior v1.4 rate, but remains low in absolute terms.

The run stopped at 5,208 logged HTTP requests for 5,207 unique provider outcomes.
Because every normal provider execution writes one outcome while a work-editions
fallback spends a second request in that execution, the one-request difference shows
that edition expansion activated at most once. The dominant remaining problem is
search returning no compatible work/edition, not insufficient edition expansion
after a work is found. Another full Open Library replay is not justified before
changing that retrieval boundary or enabling an independent provider.

## Final outcome

Open Library recall improved measurably and returned useful edition metadata, but the
work-editions endpoint does not materially solve the dominant search miss. Keep the
bounded implementation and evaluate Google Books as the next independent recall
source rather than adding more Open Library requests.
