# Scalable Work-Language Candidate Grouping

## Objective

Improve duplicate discovery before user-visible metadata groups are formed so records representing the same work in the same language can appear together despite incomplete, inconsistent, translated, conversion-generated, or otherwise poor metadata.

Author identity is the first candidate boundary. Punctuation/spacing/comma-order variants and abbreviated/full given names such as `J. K. Rowling`, `Joanne K. Rowling`, and `Rowling, J.K.` share one conservative family-plus-positional-initial identity when their available expanded given names do not conflict. Ordinary work candidates are generated only inside compatible author identities. Title, identifier, or series evidence then proposes a work relation; author similarity alone never proposes a book candidate. Known languages partition work candidates before final publication. Except for exact-binary identity, EPUB-capable inferred relations require equivalent/high-similarity content evidence before they can form a final review-only group.

The first delivery must remain generic, deterministic, local-first, explainable, and scalable to libraries with more than 20,000 records. It must not compare every pair of books and must not read sampled content for every book. Inferred groups are discovery/review-only in this milestone and cannot authorize cleanup.

## Scope

- Preserve current exact-binary and exact-normalized-metadata detectors as independent evidence sources.
- Build a versioned evidence profile for every record from already available catalog, OPF, assessment, fingerprint, series, language, and identifier facts.
- Generate a bounded candidate set with cheap indexes before any sampled-content work.
- Retain at most 20 ranked candidate records per source record before pair deduplication.
- Extract sampled visible-text landmarks only for ebook formats participating in unresolved candidate pairs.
- Use 12 deterministic landmarks of at most 64 normalized tokens per content signature.
- Cache content signatures by format fingerprint and algorithm/resource-profile versions.
- Score candidate pairs using explicit independent signals and contradictions.
- Form constrained canonical-work-and-language groups before WPF presentation.
- Prevent weak transitive links from merging otherwise incompatible records.
- Persist evidence, versions, pair decisions, group confidence, reasons, and scale metrics.
- Show inferred groups and their evidence in WPF as review-only candidates.
- Keep exact metadata cleanup eligibility separate from inferred discovery confidence.
- Add synthetic multilingual, metadata-poor, noisy-prefix, author-alias, edition-conflict, and 20,000+ record scale fixtures.
- Reserve online lookup, multilingual embeddings, and local LLM adjudication for later opt-in phases.

## Out of scope

- Automatic cleanup without explicit keeper review and per-run external-backup confirmation.
- Automatic title/author rewriting of the selected keeper.
- Replacing or weakening exact binary grouping.
- Changing exact-normalized-metadata group meaning or IDs.
- Running sampled content extraction for records that have no retained candidate pair.
- All-pairs title, author, embedding, or content comparison.
- Cloud or online lookup in the first delivery.
- Local embeddings or LLM dependencies in the first delivery.
- Translation, author, series, title, publisher, or work-specific hard-coded alias tables.
- Storing sampled sentences, complete text, or recoverable book prose.
- PDF content similarity in the first delivery; EPUB is the initial content-evidence format.
- Automatically rewriting Calibre metadata.
- Treating same work/language as proof of identical edition, revision, abridgement, illustrations, or formatting.

## Relevant requirements

- Libraries can exceed 20,000 records; candidate discovery must remain bounded and avoid $O(n^2)$ behavior.
- Metadata candidate discovery must happen before user-visible inferred groups are formed.
- Exact title/author equality is candidate evidence, not proof of content or edition equality.
- Dutch and English translations of one work should form separate language groups, not one mixed-language group.
- Missing or unknown language may be inferred only from bounded local evidence or propagated through a strong relation.
- Weak fuzzy evidence must not create large transitive clusters.
- Every inferred relation and contradiction must be explainable.
- Content evidence is an ambiguity resolver and may run only for retained candidate pairs.
- Existing OPF extraction should be reused; a second package-metadata parse is unnecessary.
- Analysis remains read-only and must not log or persist book prose.
- Long-running work is cancellable, bounded in concurrency, progress-reporting, and atomic at snapshot publication.
- Existing persisted development snapshots must continue to load. A new explicit scan is required to obtain inferred groups.
- AI remains optional and advisory and cannot trigger destructive actions.

## Existing implementation inspected

### Scan pipeline

`ScanLibraryUseCase` currently:

1. validates and reads the Calibre catalog;
2. resolves and hashes every present format;
3. assesses every EPUB and PDF;
4. detects exact-binary groups;
5. detects exact-normalized-title/author groups;
6. generates recommendations for exactly those metadata groups; and
7. publishes one complete `LibrarySnapshot`.

`LibraryScanPhase` has no candidate-generation, content-signature, pair-comparison, or inferred-clustering phases.

### Exact metadata grouping

`ExactMetadataDuplicateDetector` uses only `(NormalizedTitle, NormalizedAuthorSet)`. `MetadataTextNormalizer` performs NFC normalization, invariant casing, format-character removal, whitespace collapse, and punctuation-spacing normalization. It intentionally does not:

- remove conversion prefixes or volume/year prefixes;
- reverse comma-separated names;
- equate initials with full names;
- translate titles;
- use identifiers, OPF values, series, language, binary relations, or content; or
- infer editions and aliases.

This detector is deterministic and scales to 50,000 records. Its semantics and stable IDs must remain unchanged.

### EPUB evidence

The existing EPUB inspector already locates the actual package document through `META-INF/container.xml`; it does not assume the package is literally named `content.opf`. It extracts and persists:

- embedded title;
- creators/authors;
- languages;
- dates;
- identifiers;
- package version;
- manifest/spine/navigation facts; and
- bounded readable-character counts.

It currently discards readable text after assessment and stores no content signature. The inspector already parses all EPUBs for assessment, but this plan deliberately does not add content landmarks to that all-EPUB pass. Candidate-only content extraction remains a separate lazy operation so expensive comparison evidence is paid only for plausible candidates.

### Snapshot and projected state

`LibrarySnapshot` requires one recommendation per exact metadata group when recommendations are populated. State deltas currently recompute exact metadata groups only for metadata-changing deltas and otherwise project/remove associations. Adding inferred groups therefore requires explicit snapshot invariants, serialization, staleness, and projection rules.

Snapshot JSON uses schema `library-snapshot/1.0`; the state manifest uses `library-state/1.0`. Existing strict deserialization uses constructor shape and missing-member rejection. New optional fields must be designed so older persisted snapshots load as exact-only analysis rather than fail.

### UI and cleanup

The Metadata candidates tab currently uses exact metadata groups and offers keeper/Skip cleanup. Inferred groups cannot be silently substituted into that executable path. The first delivery must either present inferred groups in a separate review-only view or visibly disable Process for any group whose eligibility is not exact metadata.

### Scale and tests

Existing scale tests cover 50,000-record exact metadata detection and 10,000-record/6,000-delta projection. There is no candidate-cap, pair-count, content-cache, or inferred-cluster scale test.

## Proposed design

### 1. Preserve independent evidence collections

Do not replace `ExactMetadataDuplicateGroup` or mutate its normalization policy. Add a new inferred collection, tentatively:

```text
WorkLanguageCandidateGroup
```

Each inferred group represents:

```text
probable canonical work + one normalized/inferred language cohort
```

It does not claim identical edition or cleanup safety.

A record can appear in:

- an exact binary group;
- an exact metadata group;
- one inferred work-language group; and
- associated pair/evidence records.

The inferred detector runs before inferred groups are constructed and before those groups are presented in WPF.

### 2. Versioned record evidence profiles

Create one immutable `BookMatchingProfile` per current record. Profiles contain only bounded facts and digests:

- Calibre record ID;
- catalog normalized title and author variants;
- embedded OPF normalized title and author variants;
- catalog and embedded normalized language evidence;
- catalog and embedded normalized strong identifiers;
- exact-binary group IDs/fingerprints;
- normalized series name/index when present;
- publication year when present;
- dominant Unicode script for title/authors;
- selected present EPUB association/fingerprint for optional content extraction;
- evidence completeness/truncation flags; and
- analyzer/resource-profile versions.

Profiles do not contain prose.

#### Generic title variants

Retain current exact normalization and add separate candidate-only variants:

- token sequence with punctuation normalized;
- diacritic-insensitive token sequence;
- bounded generic conversion-noise trimming based on structural patterns, not book-specific words;
- optional leading ordinal/year token variant;
- token multiset and ordered token hashes.

Noise trimming must be conservative and generic. Examples of eligible patterns include a filename/application prefix followed by a delimiter and a long remaining title, or a leading standalone ordinal before a long title. Every transformation is retained as evidence with a reason code; it never rewrites stored metadata.

#### Generic author variants

Retain the exact author set and add candidate-only forms:

- punctuation-insensitive initials;
- comma-order alternative for two-part names;
- normalized family-name token;
- full-name token set;
- placeholder/unknown-author classification using a versioned multilingual placeholder vocabulary.

Placeholder authors contribute no positive identity evidence and do not create a contradiction. Initial/full-name and comma-order compatibility may generate candidates but are not decisive alone.

### 3. Language evidence

Normalize known codes to stable BCP-47-like primary language keys (`en`, `nl`, etc.) while retaining original values and provenance.

Language evidence precedence:

1. bounded visible-text language detection from candidate-only content;
2. OPF language;
3. Calibre catalog language;
4. strong-cluster propagation from a record with known language; and
5. unknown.

Script compatibility is a cheap blocker, not a language detector. Unknown language can join a known-language cluster only through a strong relation. Explicit contradictory known languages prevent same-language grouping, while still allowing a future cross-language work relationship outside this milestone.

Language detection must be generic and offline. The plan should evaluate a small proven local library or deterministic n-gram model before implementation; no hand-coded Dutch/English-only logic is allowed.

### 4. Cheap candidate indexes

Build indexes in one pass over profiles. Candidate generation is union-based and does not scan complete buckets blindly.

Proposed indexes:

- normalized validated identifier → record IDs;
- embedded identifier → record IDs, distinguishing valid global IDs from package-local UUIDs;
- exact binary group/fingerprint → record IDs;
- exact title token fingerprint → record IDs;
- rare title token → record IDs, with corpus-frequency bounds;
- author family/initial signature → record IDs;
- normalized series + rounded/index key → record IDs;
- language/script key → filtering metadata, not a stand-alone candidate source;
- title length/token-count bands → secondary filtering only.

Do not use only first-three-title characters or broad author-count buckets; those create pathological large buckets and language bias.

Each candidate edge records which indexes proposed it. Candidate pair keys are canonical ordered `(minBookId, maxBookId)` values.

### 5. Cheap pair scoring and bounded retention

Score each proposed pair without opening ebook content. Suggested evidence classes:

#### Decisive/strong local evidence

- exact binary equality;
- same validated ISBN/DOI/ASIN/OCLC;
- same repeated embedded identifier, but package-local UUIDs require corroboration;
- exact normalized metadata;
- exact embedded-title/compatible-author relation;
- same series/index plus high title/author compatibility.

#### Supporting evidence

- title token Jaccard/ordered-token similarity;
- compatible author aliases;
- compatible catalog/OPF language;
- compatible publication year;
- compatible series;
- shared uncommon title tokens;
- matching OPF title while catalog title is noisy.

#### Contradictions

- disjoint known languages for a same-language pair;
- incompatible dominant scripts without transliteration evidence;
- conflicting series indices;
- materially contradictory edition/abridgement markers;
- content signatures classified different;
- incompatible validated identifiers when both records have reliable values.

The scorer outputs integer/fixed-point components, a confidence band, reason codes, contradiction codes, and `NeedsContentEvidence`. Avoid one opaque aggregate floating-point number as the only explanation.

For each record, retain at most **20** highest-ranked cheap candidates after deterministic tie-breaking. Preserve every pair with decisive exact-binary or validated-identifier evidence even if the normal cap is reached; report cap overflow metrics. Deduplicate symmetric pairs before content work.

A global hard ceiling protects pathological metadata. Proposed initial bound:

```text
maximum unique candidate pairs = min(200,000, 10 × record count)
```

If decisive pairs alone exceed the ceiling, fail candidate discovery closed with a controlled finding rather than silently dropping strong evidence. This threshold must be verified with synthetic scale tests before freezing the policy version.

### 6. Candidate-only content signatures

Only pairs whose cheap score remains ambiguous and whose formats/languages are compatible request content evidence.

#### Demand planning

1. Deduplicate candidate pairs.
2. Select at most one preferred present EPUB per record using deterministic quality/coverage ordering.
3. Build the unique set of EPUB fingerprints required by ambiguous pairs.
4. Read each required fingerprint at most once.
5. Reuse exact-binary signatures automatically.
6. Bound extraction concurrency separately from ordinary EPUB assessment.

Records with no retained candidate pair receive no content read.

#### EPUB visible-text extraction

Use the package spine order. For each candidate EPUB:

- resolve the package document through `container.xml`;
- reject encrypted/unsafe/changed files using existing inspection boundaries;
- read only bounded XHTML/HTML spine resources;
- exclude script, style, navigation-only text, and markup;
- decode entities;
- normalize Unicode, case, whitespace, soft hyphens, line-end hyphenation, and punctuation;
- tokenize by Unicode letter/digit runs; and
- do not retain or log source text.

#### Landmark policy

Initial policy:

- 12 deterministic target positions distributed through normalized token space;
- 64 tokens per landmark;
- bounded search radius/window metadata;
- reject low-information/repeated boilerplate landmarks;
- hash strict normalized tokens and a punctuation/diacritic-tolerant variant;
- optionally retain a compact MinHash over token shingles for near-match lookup;
- store token count, chapter/spine coverage, truncation, and language result; and
- store hashes/signatures only.

Landmark selection must use token-stream positions rather than literal sentence parsing, because sentence boundaries and line wrapping vary between conversions.

#### Pair comparison

Compare landmarks symmetrically:

- A landmarks searched in bounded regions of B;
- B landmarks searched in bounded regions of A;
- require matches across multiple separated regions;
- discount front/back matter and repeated boilerplate;
- compare normalized token-count ratios and coverage;
- classify `EquivalentText`, `HighSimilarity`, `Ambiguous`, `Different`, or `Unavailable`.

Suggested initial acceptance for group discovery, subject to calibration fixtures:

- at least 8 of 12 landmarks matched in each direction;
- matches span at least 4 separated regions;
- compatible detected language;
- token-count ratio within a bounded range, initially 0.80–1.25; and
- no decisive contradiction.

`EquivalentText` and `HighSimilarity` may support discovery grouping. Neither authorizes cleanup in this milestone.

### 7. Signature cache

Add an Application-owned cache port and Infrastructure implementation outside the Calibre library.

Cache key:

```text
file SHA-256
+ file size
+ content-signature analyzer version
+ normalization version
+ landmark policy version
+ resource-profile version
```

Cache value contains only bounded signature data, language result, coverage, and safe problem codes.

Requirements:

- atomic write and strict bounded read;
- no prose;
- no library path required in the key;
- reuse one signature for byte-identical files;
- invalid/corrupt entries become cache misses;
- bounded total storage with deterministic pruning;
- cache hit/miss metrics;
- cancellation does not publish partial entries; and
- cache can be deleted without correctness loss.

Persist signatures in a separate cache rather than inflating the already-large baseline JSON with all intermediate signatures. Persist only final group/pair evidence needed to explain the snapshot.

### 8. Constrained pair graph and clustering

Build an undirected graph from accepted same-work/same-language pair decisions.

Edges have strength:

- `Anchor`: exact binary, validated work/edition identifier with compatible language, or equivalent/high-similarity content plus compatible metadata;
- `Strong`: multiple independent metadata/OPF/series/author signals, optionally confirmed by content;
- `Weak`: fuzzy metadata only; never sufficient to merge components.

Cluster algorithm:

1. Start components from anchor edges.
2. Add a record through a strong edge only if it is compatible with every anchor partition represented in the component.
3. Weak edges can rank/display possible attachments but cannot merge components.
4. Before union, validate language, script, series/index, identifier, edition-marker, and content contradictions against the complete target component.
5. Require at least one direct anchor/strong edge from every non-seed member to a stable component anchor.
6. Do not use unconstrained connected components or blind transitive closure.
7. Enforce deterministic ordering and a configurable review-size warning, not an arbitrary hard cluster-size split.

This avoids `A≈B`, `B≈C`, `A≠C` chain merges.

After canonical work components are formed, partition them by inferred normalized language. Cross-language relations may be stored as `RelatedTranslation` evidence for future UI but do not share one candidate group.

### 9. Group and evidence model

Add immutable Domain values, tentatively:

```text
BookMatchingProfile
BookCandidatePair
CandidateSignal
CandidateContradiction
ContentLandmarkSignatureSummary
ContentComparisonDecision
WorkLanguageCandidateGroupId
WorkLanguageCandidateGroup
MatchingAnalyzerVersion
MatchingPolicyVersion
```

A group contains:

- stable versioned group ID derived from policy, language, and deterministic member/evidence identity;
- normalized/inferred language and provenance;
- ordered member IDs;
- anchor member IDs;
- confidence band;
- reasons and contradictions;
- content coverage summary;
- evidence versions;
- `CleanupEligibility = ReviewOnly`; and
- optional link to exact metadata/binary groups.

Do not call these groups exact metadata groups. The existing exact group collection remains unchanged.

### 10. Cleanup eligibility separation

First delivery decision: **inferred work-language groups are discovery/review-only**.

- `ExecuteBulkMetadataCandidateCleanupUseCase` continues to accept only exact metadata group IDs.
- WPF inferred groups have no Process command and no mutation request conversion.
- Exact groups may be shown within or linked from inferred groups, but executable selection is visibly distinguished.
- Architecture tests prohibit inferred group IDs/types from appearing in cleanup request contracts.
- A later separately approved milestone may promote a subset after measured acceptance, edition safeguards, and disposable-library qualification.

This prevents sampled similarity or fuzzy metadata from silently becoming deletion authority.

### 11. Scan orchestration

Proposed phase order:

```text
Catalog read
→ file resolution/hash
→ EPUB/PDF assessment (existing OPF evidence)
→ exact binary grouping
→ exact metadata grouping
→ build matching profiles
→ generate cheap candidate pairs
→ score cheap evidence
→ plan candidate-only content signatures
→ extract/cache required EPUB signatures
→ compare candidate content
→ constrained work clustering
→ language partitioning
→ inferred group publication
→ exact-group recommendation generation
→ snapshot publication
```

Add explicit `LibraryScanPhase` values and safe progress metrics:

- `BuildingMatchingProfiles`
- `GeneratingBookCandidates`
- `ExtractingCandidateContentEvidence`
- `ComparingCandidateContent`
- `ClusteringWorkLanguageCandidates`

Progress reports:

- profiles built / records;
- retained candidates / cap and records capped;
- unique candidate pairs;
- signatures required, cache hits, extracted, unavailable;
- content pairs compared;
- groups accepted and ambiguous records left ungrouped.

All stages propagate cancellation. Snapshot publication remains atomic; cancellation/failure leaves prior UI state unchanged.

### 12. Snapshot, persistence, and migration

Extend `LibrarySnapshot` with a new optional ordered inferred-group collection and scan matching summary. Keep exact collections and recommendation invariants unchanged.

Compatibility requirements:

- Old exact-only snapshots load with empty inferred groups and a `MatchingEvidenceUnavailable` summary.
- UI states that a fresh Scan is required for expanded discovery.
- Do not automatically parse or reanalyze library files during Load.
- Snapshot serializer remains strict for known fields but treats newly introduced collections as optional/default-empty when reading `library-snapshot/1.0`, or introduce a deliberate `library-snapshot/1.1` compatibility reader.
- State baselines/checkpoints persist inferred final groups, not every signature.
- State delta projection removes deleted records from inferred groups and marks affected inferred evidence stale; it does not run content inspection or candidate discovery.
- Metadata-changing deltas must not recompute inferred groups using cheap exact logic. They mark matching evidence stale and require explicit Scan for refresh.
- Group IDs and evidence versions must survive deterministic round-trip tests.

A schema/constructor compatibility spike is the first implementation task because current strict Newtonsoft constructor binding and missing-member handling can reject shape changes.

### 13. WPF presentation

First delivery adds a separate review-only view, tentatively `Expanded candidates`, rather than replacing executable Metadata candidates immediately.

Display:

- canonical/display title candidates;
- inferred language and provenance;
- members;
- confidence;
- exact/identifier/OPF/content evidence;
- contradictions and warnings;
- content coverage/cache status;
- related exact groups; and
- explicit `Review only — cleanup unavailable` state.

Users can double-click members through the existing viewer launcher. No keeper/Remove actions appear for inferred groups in phase one.

After acceptance, a later UX decision may merge exact and inferred discovery into one tab while preserving separate eligibility.

### 14. Online lookup, embeddings, and local LLM phases

#### Phase two: optional online work enrichment

Potential providers: Open Library and Google Books, subject to API/license review.

Requirements:

- explicit opt-in and network disclosure;
- query validated ISBN/edition identifiers first;
- bounded requests, timeout, retry, rate limiting, and offline behavior;
- provider/version/timestamp provenance;
- cache by provider + query + response schema version;
- no ebook text upload;
- lookup failures never fail local scan; and
- provider evidence remains advisory unless corroborated locally.

Online edition-to-work mappings can connect different ISBN editions of one work but language still partitions groups.

#### Phase three: optional local multilingual embeddings

Use a version-pinned local ONNX model or separately managed local service. Embed title/author/series metadata only in the initial AI phase, not book prose. Use approximate nearest-neighbor retrieval to propose candidates under the same 20-per-record cap. Embeddings are candidate generators, never decisive evidence.

#### Phase four: optional local LLM adjudication

Use a closed candidate list and structured output. Inputs are bounded metadata and reason codes; sampled prose is excluded by default. Record model/version/prompt policy and confidence. LLM output cannot create cleanup eligibility or bypass deterministic contradictions.

### 15. Performance and resource targets

For a 20,000-record synthetic library:

- no all-pairs loops or allocations;
- at most 20 ordinary retained candidates per record;
- at most 200,000 unique pairs under the initial global ceiling;
- content signatures only for records in ambiguous retained pairs;
- each unique file fingerprint extracted at most once per version;
- content extraction concurrency default 2, maximum 4;
- 12 landmarks × 64 tokens per signature;
- no stored prose;
- cheap profile/index/pair generation target under 10 seconds on the development machine after catalog/assessment data exists;
- inferred matching target under 20% additional wall time for a scan with warm signature cache;
- cold content extraction reports its separate cost rather than hiding it in grouping;
- peak matching memory target under 256 MiB beyond the current snapshot on a 20,000-record synthetic fixture; and
- persisted final inferred evidence target under 25% snapshot-size growth, with signatures in a separate cache.

These are acceptance targets, not unverified guarantees. Benchmark results and hardware details must be recorded in the plan during implementation.

### 16. Scale metrics

Persist a bounded `BookMatchingRunSummary` with:

- record/profile count;
- index bucket counts and maximum bucket size;
- proposed cheap pairs;
- retained pairs after per-record cap;
- records whose candidates were capped;
- decisive-pair overflow count;
- content signatures requested;
- cache hits/misses/extraction failures;
- content comparisons;
- accepted anchor/strong/weak/rejected pair counts;
- inferred group count and size distribution;
- unknown-language count; and
- total phase durations.

Do not store titles, authors, identifiers, paths, or content in metrics/logs.

## Files expected to change

### Domain

- `src/CalibreLibraryCleaner.Domain/Duplicates/MetadataTextNormalizer.cs` — candidate-only variants without changing exact normalization.
- New matching profile, signal, contradiction, pair, content-summary, group, ID, version, and run-summary types under `Domain/Duplicates` or a new `Domain/Matching` namespace.
- New cheap candidate generator, fixed-point pair scorer, and constrained cluster policy.
- `src/CalibreLibraryCleaner.Domain/Libraries/LibrarySnapshot.cs` — optional inferred groups and run summary.
- `src/CalibreLibraryCleaner.Domain/Libraries/LibraryStateDelta.cs` — stale/remove projection rules only; no reanalysis.

### Application

- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanPhase.cs` — matching phases.
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryAnalysisOptions.cs` — candidate/content limits and bounded concurrency.
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs` — orchestration and atomic failure mapping.
- Existing EPUB assessment association indexing for profile creation.
- New candidate matching orchestration contracts/use cases.
- New candidate-only content inspector/cache ports and provider-neutral request/results.

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/Epub/VersOneEpubInspector.cs` — either share provider-neutral safe text extraction helpers or add a separate candidate-content inspector using identical archive/path/resource limits.
- New versioned file-fingerprint content-signature cache outside the library.
- `src/CalibreLibraryCleaner.Infrastructure/LibrarySnapshots/LibrarySnapshotJsonSerializer.cs` — backward-compatible inferred-group/run-summary persistence.
- `src/CalibreLibraryCleaner.Infrastructure/LibrarySnapshots/VersionedJsonLibrarySnapshotStore.cs` and state store only as needed for schema bounds/pruning.
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` — matching/cache registrations.

### WPF

- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs` — inferred group presentation and progress.
- New review-only inferred group/member/reason row ViewModels.
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml` — Expanded candidates tab and no cleanup action.
- Existing Calibre viewer double-click reused for member inspection.

### Tests and documentation

- New Domain tests for profile normalization, identifiers, candidate caps, scoring, contradictions, clustering, language partitioning, and determinism.
- New Application tests for lazy signature demand, cache reuse, cancellation, progress, and atomic failure.
- New Infrastructure tests for bounded visible-text extraction, no-prose cache, corruption, reparse/path limits, and round trips.
- New WPF tests for review-only groups, evidence, stale snapshot messaging, and viewer opening.
- Architecture tests for no process/filesystem leakage and no inferred cleanup conversion.
- `docs/adr/0019-scalable-work-language-candidate-discovery.md`.
- `docs/duplicate-detection.md`, `docs/domain-model.md`, `docs/architecture.md`, `docs/functional-requirements.md`, `docs/test-strategy.md`, `docs/roadmap.md`, and `README.md`.

## Safety considerations

- Exact detectors and cleanup eligibility remain unchanged.
- Inferred groups are review-only and cannot enter mutation request contracts.
- Sampled content evidence proves similarity, not identical edition.
- Distinct known languages cannot enter one same-language group.
- Weak edges never merge components.
- Pair and candidate limits fail closed with visible findings/metrics.
- Candidate content extraction is read-only, bounded, cancellable, and performed only for demanded fingerprints.
- No source prose, sentence text, token windows, physical paths, or book metadata is logged.
- Caches are outside the library, contain no prose, are disposable, and cannot authorize cleanup.
- Online/model phases remain separately opt-in and are not part of the first implementation.
- Existing loaded snapshots never trigger implicit scans or content reads.
- Projected mutation state never runs candidate discovery; explicit Scan is the only refresh authority.

## Implementation steps

### Phase 0: compatibility and benchmark harness

1. Add this plan and draft ADR 0019; do not change grouping behavior yet.
2. Build a serializer compatibility spike proving old `library-snapshot/1.0` and current state baseline/checkpoint artifacts load with empty inferred collections.
3. Add a deterministic 20,000-record benchmark fixture with configurable candidate density, languages, missing metadata, identifier collisions, and title/author noise.
4. Record current grouping wall time, scan wall time, peak managed memory, and snapshot size on the development machine.

### Phase 1: profile and cheap candidate generation

5. Add versioned profile/evidence values and candidate-only title/author variants while preserving exact normalizer tests byte-for-byte.
6. Add generic language-code normalization and Unicode script evidence.
7. Centralize strong identifier normalization currently duplicated in recommendation/retention code; preserve checksum behavior with golden tests.
8. Implement bounded inverted indexes, canonical pair IDs, deterministic cheap signals/contradictions, per-record top-20 retention, decisive-edge overflow handling, and run metrics.
9. Add scale/adversarial tests for common-title mega-buckets, unknown authors, repeated bad UUIDs, identifier collisions, and cancellation.

### Phase 2: candidate-only content signatures

10. Define versioned provider-neutral content-signature request/result/limits and cache ports.
11. Implement safe EPUB spine visible-text token streaming without storing prose.
12. Implement 12×64-token deterministic landmark selection, boilerplate rejection, language detection, signature hashing, coverage, and safe problem codes.
13. Implement atomic no-prose cache keyed by file fingerprint and all policy/resource versions.
14. Add demand planner proving only ambiguous retained candidate fingerprints are inspected and each unique fingerprint is read once.
15. Add symmetric landmark comparison and calibrated `EquivalentText`/`HighSimilarity`/`Ambiguous`/`Different` decisions.

### Phase 3: constrained grouping

16. Implement fixed-point pair decisions and component-aware union constraints.
17. Partition accepted work components by normalized/inferred language.
18. Generate deterministic review-only `WorkLanguageCandidateGroup` values and run summary.
19. Add generic multilingual fixtures using synthetic/public-domain text transformations, unrelated same-title controls, editions with front/back matter, punctuation/hyphenation changes, and weak-chain adversarial graphs.
20. Add a regression fixture modeled on the observed failure pattern without using copyrighted prose, work-specific aliases, or production-library data.

### Phase 4: scan, persistence, and UI

21. Integrate explicit matching phases after existing exact grouping/assessment evidence is available and before inferred group publication.
22. Extend snapshot/state persistence with backward compatibility, strict bounds, stable versions, and stale projection rules.
23. Add an Expanded candidates review-only WPF tab with reasons, contradictions, language provenance, content coverage, scale summary, and member viewer support.
24. Ensure no inferred group type is accepted by exact or metadata cleanup requests.
25. Validate cancellation leaves the previous UI snapshot untouched.

### Phase 5: acceptance and future gates

26. Run full restore/build/test/format/diff/vulnerability verification.
27. Benchmark 20,000 and 50,000 record synthetic libraries, cold and warm content caches, sparse and pathological candidate distributions.
28. Compare inferred groups against a manually labeled generic evaluation corpus; record precision, recall, language partition accuracy, and ambiguous/unmatched counts.
29. Require explicit product acceptance before any later cleanup-eligibility milestone.
30. Create separate plans/ADRs for online lookup, embeddings, and local LLM phases only after local deterministic results are measured.

## Tests

### Domain

- Exact metadata normalization/group IDs remain unchanged.
- Candidate-only title variants handle punctuation, diacritics, generic conversion prefixes, and ordinal prefixes without book-specific rules.
- Author variants handle comma order and initials conservatively; placeholders add no positive evidence.
- ISBN-10/13, DOI, ASIN, OCLC, and embedded ID normalization is deterministic and rejects malformed values.
- Candidate generation is deterministic under shuffled input.
- At most 20 ordinary candidates are retained per record.
- Decisive evidence is not silently dropped by the ordinary cap.
- Pathological buckets hit a controlled global bound.
- Fixed-point scores and reason ordering are stable across cultures.
- Known language contradictions reject same-language edges.
- Unknown language propagates only through strong evidence.
- Weak transitive chains do not merge clusters.
- Complete-component contradictions block union.
- Group IDs and member ordering are deterministic.

### Content extraction/comparison

- Only demanded candidate fingerprints are opened.
- Exact-binary files share one cached signature.
- Spine order, markup removal, entities, whitespace, line breaks, soft hyphens, and line-end hyphenation normalize deterministically.
- Scripts/styles/navigation and repeated boilerplate do not create decisive landmarks.
- Twelve landmarks are distributed and bounded to 64 tokens.
- No prose appears in Domain values, cache JSON, logs, exceptions, or snapshot artifacts.
- Same text with markup/punctuation/front-matter changes matches.
- Different text with shared boilerplate does not match.
- Symmetric comparison is required.
- Truncation, malformed EPUB, encryption, limits, missing files, changed files, and cancellation return controlled unavailable/ambiguous evidence.
- Cache corruption becomes a miss; cancellation publishes no partial entry.

### Application/integration

- Scan phases and progress metrics are monotonic and bounded.
- Candidate content extraction is skipped when cheap evidence accepts/rejects a pair.
- Cache hits avoid file inspection.
- Failure/cancellation leaves prior snapshot visible.
- Recommendations remain one-per-exact-group and unchanged.
- Existing snapshots load with inferred evidence unavailable.
- State deltas remove/stale inferred associations without scanning.

### WPF/architecture

- Inferred groups show work/language confidence, reasons, contradictions, and review-only status.
- No Process candidates command is enabled for inferred groups.
- Member double-click opens the selected present format.
- Old snapshots display a fresh-scan-required message for expanded matching.
- Domain/Application remain free of filesystem, parser, process, WPF, and provider model types.
- Architecture tests prohibit inferred group IDs in mutation request contracts.

### Scale and evaluation

- 20,000-record sparse fixture meets candidate and memory bounds.
- 20,000-record pathological common-title fixture stops at the global pair bound without unbounded allocation.
- 50,000-record cheap-only fixture remains deterministic.
- Warm-cache run opens zero cached candidate EPUBs.
- Evaluation corpus includes multiple languages, scripts, title variants, author variants, same-title unrelated works, translations, editions, abridgements, and malformed metadata.
- Precision is prioritized over recall; initial acceptance target is at least 99% precision for automatically formed inferred groups on the labeled corpus. Recall and ungrouped records are reported, not optimized by weakening contradictions.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Additional verification:

- targeted Domain matching suites;
- candidate-only content inspector/cache suites;
- architecture tests;
- WPF startup/binding tests;
- deterministic snapshot golden/round-trip tests;
- no-prose searches over generated cache/snapshot/log fixtures;
- 20,000/50,000-record benchmark harness with recorded hardware/runtime;
- optional manual review against copied/synthetic fixtures only, never destructive cleanup of the real library.

## Risks

- Aggressive title/author normalization can produce false candidates or clusters. Keep candidate variants separate from exact identity and require corroboration.
- Package-local UUIDs can be copied across unrelated conversions. Treat them as corroborating evidence unless validated as global identifiers.
- ISBNs identify editions, not necessarily works; different ISBNs are not automatically contradictions and same ISBNs do not override language/content conflicts.
- Content samples can miss changed chapters or overmatch boilerplate. Require distributed symmetric landmarks and preserve ambiguity.
- Same work/language can still contain abridged, revised, illustrated, censored, or annotated editions. First delivery is review-only.
- Language detection on short or front-matter-heavy samples can be wrong. Retain provenance/confidence and permit unknown.
- Candidate caps can reduce recall. Record capped metrics and preserve decisive evidence rather than silently increasing limits.
- Signature extraction can dominate cold scans in dense libraries. Demand only ambiguous retained pairs, deduplicate fingerprints, cache, and report separate progress.
- Snapshot growth can worsen development Load time. Persist final evidence only and keep signatures outside the baseline.
- Strict constructor-based JSON deserialization makes schema evolution risky. Complete the compatibility spike first.
- State projection can leave inferred evidence stale after mutation. Never recompute by reading files; mark stale and require explicit Scan.
- A local language library or future embedding dependency may introduce package/runtime/licensing issues. Evaluate and approve separately.
- Online services can be unavailable, rate-limited, wrong, or privacy-sensitive. Keep them optional and out of phase one.
- LLM output can be inconsistent or hallucinated. Keep it advisory and unable to authorize cleanup.

## Unresolved questions

The following are implementation calibration questions, not blockers for the architectural plan:

1. Which offline language detector meets accuracy, package size, license, determinism, and bounded-memory requirements? Evaluate at least one n-gram library and a minimal in-house model on the labeled corpus before selecting.
2. Which generic conversion-prefix patterns are safe enough for candidate generation? Freeze only patterns that improve recall without reducing the precision target.
3. Which MinHash/shingle parameters add value beyond distributed landmark hashes? Benchmark before including them in schema version 1.
4. Are the initial 8-of-12 landmark and 0.80–1.25 token-ratio thresholds appropriate? Calibrate on synthetic/public-domain editions and holdout data.
5. Should inferred groups link cross-language translations in a future work-level view? This first delivery stores language-separated groups only.
6. Should the WPF first delivery use a separate tab or a unified tab with eligibility badges? The plan defaults to a separate review-only tab for safety.

## Progress

- [x] Current scan, OPF evidence, exact grouping, snapshot/state persistence, cleanup boundary, WPF, scale tests, and roadmap inspected.
- [x] Example snapshot analyzed to identify generic failure patterns without hard-coded aliases.
- [x] Product decisions recorded: review-only inferred groups, local deterministic first delivery, 20 candidates per record, and 12×64-token landmarks.
- [x] Detailed architecture and phased execution plan written.
- [x] ADR 0019 drafted and accepted.
- [x] Compatibility spike and benchmark baseline completed.
- [x] Phase 1 profile normalization and bounded cheap candidate generation completed.
- [x] Local deterministic matching implementation completed.
- [x] Candidate-only content evidence completed.
- [x] Constrained grouping and expanded WPF completed.
- [ ] Dedicated content-confirmed expanded cleanup completed.
- [ ] Complete verification and measured acceptance completed.

## Final outcome

Implementation started on 2026-08-08. Phase 0 completed without changing grouping behavior. Exact-only JSON with the new matching fields removed loads successfully with empty inferred groups and `MatchingEvidenceStatus.Unavailable`; populated inferred evidence round-trips canonically with stable IDs and review-only eligibility.

The opt-in 20,000-record baseline ran on the development machine with 10,000 exact metadata groups: exact grouping took 406 ms and allocated 112,930,776 managed bytes; canonical snapshot serialization took 1,369 ms and produced 23,055,465 bytes. The developer-observed real-library full scan remains approximately twenty minutes and is dominated by file analysis rather than exact grouping. These figures are comparison baselines, not timing assertions.

Phase 1 adds bounded candidate-only metadata profiles and deterministic inverted-index generation. On the same 20,000-record fixture, profile construction took 911 ms, cheap candidate generation took 352 ms, and 10,000 unique candidate pairs were retained from 20,000 directed proposals. No records hit the ordinary top-20 cap and the effective global ceiling was not exceeded. The matching values retained 29,731,152 managed bytes while transient allocation was 601,882,408 bytes (450,639,528 profile construction and 151,242,880 candidate generation). The retained-memory and wall-time targets passed; transient normalization allocation remains a later optimization opportunity rather than persisted or peak state.

The first cold-content implementation exposed unacceptable large-library behavior: each token shingle created SHA-256/UTF-8 allocations, all spine chapters were parsed twice, every cache write enumerated and revalidated the complete cache, author-only relations could demand nearly every EPUB, one-sided pairs inspected an EPUB even when its counterpart had no usable EPUB, and contradictory language/series pairs still requested content that could not affect grouping. The corrected `epub-content-signature/1.1.0` policy uses an allocation-free deterministic 64-bit bottom-k sketch, rereads only chapters intersecting the 12 landmark windows, requires work-level evidence and two comparable EPUBs, skips deterministic contradictions, defaults to four bounded content workers, writes disposable cache entries without write-through durability, and prunes once per resolver batch. Structured diagnostics now separate planning, cache read, preflight/counting/sampling inspection, cache write, pruning, and total elapsed time without logging paths or book metadata. Chapter-level UI status is throttled to readable intervals.
