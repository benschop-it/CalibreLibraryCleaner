# Open Library Bibliographic Evidence

## Objective

Implement the first genuine online bibliographic evidence source. Use low-volume,
cache-first Open Library work lookup to confirm unresolved same-work/language
Candidate pairs while preserving deterministic local fallback, 100% corpus
precision, bounded resources, privacy disclosure, and separation from mutation.

## Scope

- Add provider-neutral Domain values for bibliographic queries, bounded work
  candidates, deterministic per-record resolutions, pair evidence, provenance, and
  policy versions.
- Resolve only Candidate pairs that remain weak because local content is ambiguous or
  unavailable; skip anchors, locally confirmed pairs, decisive contradictions, and
  unrelated records.
- Prefer validated ISBN lookup when present; otherwise send bounded title, author,
  and optional normalized language fields to Open Library Search API.
- Require a unique or clearly dominant provider work candidate under deterministic
  title/author/language compatibility before resolving a record.
- Add same-provider/same-work Anchor evidence to a pair. Different, ambiguous,
  unavailable, or failed lookups remain neutral and cannot block local matching.
- Implement an enabled-by-default Open Library Infrastructure client with a fixed
  HTTPS endpoint, descriptive User-Agent, optional configured contact, one-request-
  per-second unidentified rate limit, bounded response/results, no retries, and
  controlled problem codes.
- Add a versioned, query-hash keyed, atomic bounded cache outside the library. Cache
  stores reduced resolutions and provenance, never raw provider payloads or raw
  query metadata.
- Add truthful provider progress and aggregate request/cache/match/failure summary
  counts; disclose transmitted field categories in Candidate progress/UI summary.
- Extend the offline corpus schema with provider-resolution oracles only for the two
  independently sourced public-domain work families. Do not claim Open Library knows
  synthetic titles.
- Measure calibration and frozen-holdout deltas, update the reviewed baseline, and
  document actual results.

## Out of scope

- Live network calls in automated tests or baseline generation.
- Bulk catalog enrichment, background harvesting, prefetching every library record,
  or more than 24 cache misses per user-triggered Candidate analysis.
- API credentials, authenticated Open Library writes, covers, descriptions, subjects,
  ratings, availability, ebook content, or provider payload retention.
- Treating provider disagreement or absence as a contradiction.
- Adding another provider, generalized provider selection UI, local AI/embeddings, or
  PDF/visual matching in this slice.
- Changing local candidate generation, content comparison, keeper policy, or mutation.
- Fabricating provider results for synthetic corpus families.

## Relevant requirements

- Configured online bibliographic providers are enabled by default and disclose
  provider plus transmitted field categories.
- Provider failure falls back to local evidence and never blocks publication.
- Every result/cache identity includes complete query identity and provider/policy
  versions; corruption/loss is a miss.
- Evidence provenance, coverage, contradictions, and deterministic IDs remain
  explicit.
- No provider output directly authorizes mutation; Domain fusion remains decisive.
- Logs contain no title, author, identifier, query, response payload, path, or work ID.
- Matching changes require calibration and frozen-holdout evidence.

## Existing implementation inspected

- `DiscoverWorkLanguageCandidatesUseCase` builds profiles/candidates, resolves local
  content, decides pairs, and clusters groups. Bibliographic resolution belongs after
  content comparison and before final decisions.
- Current `CandidateEvidence` carries only code/strength; bounded optional source
  provenance is required for provider evidence.
- `BookMatchingRunSummary` has local content metrics but no provider counts.
- Infrastructure has no HTTP client/provider implementation. Existing file caches use
  canonical external roots, reparse checks, bounded JSON, atomic replacement, and
  aggregate logging.
- WPF composition registers Application use cases explicitly; Infrastructure owns
  adapter registrations.
- Open Library officially supports low-volume human-triggered lookup, requests
  caching, asks for an identifying User-Agent/contact, limits unidentified clients to
  one request/second, and prohibits bulk harvesting/hundreds of single-book requests.
- Search API returns work-level `key`, title, authors, languages, and edition count;
  fields/limit can bound responses.
- Current 12 false negatives all reach candidates. Ten are synthetic ambiguous-
  content variants and remain outside provider evaluation. The sourced families map
  to Open Library works `/works/OL138052W` (Alice) and `/works/OL66554W` (Pride).

## Proposed design

### Domain values and policy

Add:

- `CandidateEvidenceProvenance(SourceId, SourceVersion, ResultId)` as an optional
  bounded property on `CandidateEvidence`;
- `BibliographicProviderIdentity`, `BibliographicQueryFields`,
  `BibliographicLookupQuery`, `BibliographicWorkCandidate`,
  `BibliographicResolutionStatus`, and `BibliographicWorkResolution`;
- `BibliographicResolutionPolicy.Resolve(profile, query, candidates, retrievedAt)`;
- `BibliographicPairEvidencePolicy.Enrich(pairs, resolutions)`.

Resolution accepts a candidate only when title similarity remains work-bearing,
author identity is compatible, known language does not conflict, and either exactly
one candidate qualifies or the top candidate is exact-title/author compatible and
has at least five editions plus at least four times the edition count of every other
qualifying work. The result stores only provider/work IDs, query hash/field flags,
retrieval time, status, and policy versions.

Pair enrichment adds `MATCH.BIBLIOGRAPHIC.SAME_WORK` at Anchor strength only when
both records have `Matched` resolutions from the same provider/version and identical
work ID. Existing decisive contradiction logic can still reject the pair.

### Application orchestration

Add ports:

- `IBibliographicProvider.SearchAsync(query)` returning bounded candidates/status;
- `IBibliographicResolutionCache.TryReadAsync/WriteAsync/PruneAsync`.

`ResolveBibliographicEvidenceUseCase`:

1. selects pair members whose local comparison is `Ambiguous` or `Unavailable` and
   whose pair is not already an anchor/rejected by known decisive contradiction;
2. builds one canonical query per distinct record fact set;
3. reads cache first and considers entries fresh for 30 days;
4. sends at most 24 deterministic cache-miss queries per run, sequentially;
5. converts responses through Domain resolution policy and writes reduced results;
6. converts all provider/cache failures to unavailable aggregate outcomes;
7. reports completed/total/cache/request/match/failure counts without metadata; and
8. enriches pairs before final Domain decision and clustering.

Provider services remain optional for direct use-case construction; production DI
always supplies the enabled-by-default configuration.

### Open Library adapter

- Fixed base URI `https://openlibrary.org/`; redirects disabled.
- Search endpoint only; query parameters encoded with `Uri.EscapeDataString`.
- Fields limited to `key,title,author_name,language,edition_count`; `limit=5`.
- Valid ISBN uses `isbn`; otherwise title/author and optional language preference.
- Singleton `HttpClient`/handler, 10-second request timeout, response headers first,
  256 KiB body ceiling, JSON depth 16, at most five candidates, bounded strings/lists.
- No automatic retry. HTTP/network/timeout/malformed/oversize become controlled
  unavailable results.
- Singleton rate gate enforces at least one second between requests unless a contact
  is configured, then at most three/second. Cancellation is honored for network and
  rate-delay resource release.
- User-Agent identifies CalibreLibraryCleaner and repository URL; optional
  `CALIBRE_OPEN_LIBRARY_CONTACT` adds contact without logging it.

### Cache

`open-library-resolution-cache/1.0`, keyed by SHA-256 of provider identity/version,
resolution policy version, normalized request fields, and query schema. Default root
is `%LOCALAPPDATA%/CalibreLibraryCleaner/bibliographic-resolutions`, with 32 KiB per
entry and 64 MiB total. Corrupt/stale/version-mismatched entries are misses. Writes
are atomic; pruning removes oldest physical files. Documents contain no raw query or
provider candidate payload.

### Corpus and acceptance

Add optional per-record provider resolution oracles with provider/version, work ID,
query fields, retrieval timestamp, and status. Only `cal-wikidata-q92640` and
`hold-wikidata-q170583` receive Open Library matched identities. Evaluator enriches
pairs through the real Domain pair policy; no HTTP/Application dependency is added.

Acceptance:

- precision stays 100% overall and in both splits;
- recall improves from 94.8276% to at least 95.6897% overall (one recovered pair per
  split);
- candidate-route recall remains 100%;
- zero false positives, overmerges, cross-language merges, or unknown gaps;
- exactly Alice/Pride opaque pairs leave the false-negative set;
- ten synthetic `CONTENT_UNAVAILABLE_OR_WEAK` pairs remain unchanged;
- provider cache warm run performs zero HTTP calls in focused tests.

## Files expected to change

- Domain matching evidence/provider values and policies
- Application provider ports, options, resolution use case, discovery orchestration
- Infrastructure Open Library client/options/cache and DI
- WPF composition plus provider progress/summary display
- Domain/Application/Infrastructure/WPF/architecture tests
- corpus schema, sourced scenarios, evaluator, baseline, and source provenance
- `docs/architecture.md`, `docs/functional-requirements.md`,
  `docs/duplicate-detection.md`, `docs/test-strategy.md`, `docs/roadmap.md`
- this plan and `docs/plans/README.md`

## Safety considerations

- HTTP is read-only and cannot reach any mutation port.
- Only bounded bibliographic fields are transmitted; no paths, file data, content,
  cache keys, or credentials.
- Query values and provider payloads are never logged.
- Cache keys are one-way hashes; cache values contain reduced work identity evidence.
- Failure, timeout, 429, malformed JSON, corruption, and cache loss all degrade to
  existing local results.
- No live network dependency exists in ordinary tests.
- Request/run bounds and provider rate limits prevent bulk use.

## Implementation steps

1. Add Domain provider values, deterministic resolution/pair policies, provenance,
   and focused tests.
2. Extend corpus contract/oracles for the two sourced families and prove expected
   offline quality delta.
3. Add Application ports/options/use case with bounded cache-first orchestration and
   fake-based cold/warm/failure/progress tests.
4. Integrate optional enrichment into discovery and add provider run-summary fields.
5. Add bounded Open Library HTTP adapter and atomic resolution cache with fake-handler
   and filesystem tests.
6. Register enabled-by-default production services and expose provider disclosure in
   progress/summary UI.
7. Bump matching/Candidate workflow policies if pair/group behavior changes, then
   review calibration/holdout deltas and publish the semantic baseline.
8. Reconcile docs, archive the plan, and run complete verification.

## Tests

- query/candidate/resolution bounds and canonical query hash;
- exact/compatible title-author-language matching, ambiguous duplicates, dominant
  canonical work, invalid work IDs, and unknown language;
- same provider/work adds Anchor provenance; different/unavailable/ambiguous remains
  neutral; decisive local contradictions still reject;
- only unresolved pair members are queried; request cap/order/dedup deterministic;
- cold miss writes cache; warm run makes zero provider calls; stale/corrupt is miss;
- timeout/HTTP/429/malformed/oversize/provider/cache failure leaves local result;
- no log/assertion message contains bibliographic fields or payloads;
- Open Library URI has only disclosed fields, `fields`, and `limit=5`; headers/rate
  gate/response bounds enforced with fake handlers and clock/delay abstractions;
- corpus recovers only Alice/Pride while retaining 100% precision;
- run summary and WPF progress disclose provider, field categories, cache and failure
  counts without metadata;
- Domain/Application/Infrastructure/WPF dependency direction and no mutation access.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj --no-build
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --no-build
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --no-build
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

## Risks

- Open Library contains duplicate work records. Unique/dominant deterministic
  resolution and neutral ambiguity prevent blind trust in first-result ranking.
- Title/author queries disclose ordinary metadata. Candidate progress/UI explicitly
  discloses field categories; users can disable the provider through configuration.
- Cold runs can add seconds. Cache-first targeting, 24-request cap, provider rate
  gate, progress, and non-blocking fallback bound impact.
- Optional contact configuration affects rate but is not a secret and is never logged.
- Additive evidence provenance/summary fields affect persisted JSON. Constructors use
  compatible defaults; workflow policy versioning prevents stale mutation authority.
- The corpus demonstrates provider policy value only on two sourced works. It does
  not claim universal provider accuracy; synthetic cases remain deliberately
  unresolved.

## Unresolved questions

- A future settings UI may provide provider toggles/contact instead of environment or
  composition options. This slice requires visible operational disclosure and a
  configuration switch, not a complete settings subsystem.
- Open Library data licensing/attribution text must be confirmed in final docs before
  release; only stable work IDs and reduced evidence are persisted.

## Progress

- [x] Existing matching/orchestration/cache/DI surfaces inspected.
- [x] Open Library API usage/rate guidance reviewed.
- [x] Conservative vertical-slice design and corpus honesty rule selected.
- [x] Domain evidence/resolution policy implemented.
- [x] Offline corpus provider delta measured.
- [x] Application orchestration and cache ports implemented.
- [x] Infrastructure client/cache and production DI implemented.
- [x] UI disclosure/run metrics implemented.
- [x] Baseline/docs updated and complete verification passed with 676/676 tests.

## Final outcome

Implemented `work-language-matching/1.4.0` and `candidate-analysis/1.2.0` with
provider-neutral resolution/provenance, cache-first bounded orchestration, an
enabled-by-default Open Library Search adapter, reduced atomic cache, aggregate run
metrics, progress/field disclosure, environment configuration, and local fallback.
Automated tests use fake HTTP/provider/cache boundaries only; no ordinary test uses
the live network.

Calibration and frozen holdout changed identically:

- precision: unchanged at 100%;
- recall: 94.8276% to 95.6897%;
- F1: 97.3451% to 97.7974%;
- candidate-route recall: unchanged at 100%;
- false positives: unchanged at 0;
- false negatives: 6 to 5 per split, 12 to 10 overall; and
- keeper coverage: 94.3396% to 95.2830%.

Exactly the sourced Alice and Pride pairs left
`CONTENT_UNAVAILABLE_OR_WEAK`. Ten synthetic ambiguous-content pairs intentionally
remain because Open Library is not claimed to know synthetic works. Cold/warm
Application tests prove a warm run performs zero provider requests; adapter tests
cover timeout/HTTP/oversize parsing, exact field transmission, contact redaction,
cache privacy/corruption/version misses, and thrown-adapter fallback.

The official Open Library API usage/rate documentation was reviewed and is cited.
The linked licensing endpoint returned HTTP 503 through both available retrieval
paths on 2026-08-15, so documentation makes no specific license claim. The product
stores no response payload and only persists factual work IDs plus reduced evidence
provenance. Reconfirm provider licensing text before a packaged release.
