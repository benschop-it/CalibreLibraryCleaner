# Priority 1: Matching Corpus and Baseline Measurement

## Objective

Create the first versioned, reproducible evaluation system for the current
same-work/language matcher. Build a labeled calibration/holdout corpus, execute the
existing matching pipeline without changing its policies, and record candidate-
generation recall, final grouping precision/recall, error categories, language
partitioning, keeper accuracy, cap effects, determinism, and evaluation cost.

The output becomes the baseline against which every later normalization, PDF,
cover/visual, online-provider, local-model, and evidence-fusion change is judged.

## Scope

- Define a strict `matching-corpus/1.0` JSON format containing bounded raw record
  facts, same-work/language ground truth, content-comparison oracle facts, tags,
  acceptable keepers, split, source, and licensing/provenance.
- Add committed calibration and holdout corpora made only from synthetic/generated
  records and curated public-domain bibliographic examples.
- Add a test-owned evaluator that runs the current pipeline from corpus records
  through:
  1. Exact Metadata detection;
  2. matching-profile construction;
  3. cheap candidate generation;
  4. oracle content comparison for requested pairs;
  5. pair decisions;
  6. work-language clustering;
  7. Unified Candidate merging; and
  8. generated-keeper selection.
- Calculate deterministic overall and per-category metrics and stage-specific failure
  reasons.
- Commit a `matching-evaluation/1.0` baseline report bound to complete corpus digests
  and current matching/unified policy versions.
- Add automated regression tests that compare current evaluation with the reviewed
  baseline and emit stable scenario IDs for every changed false positive/negative.
- Support an optional external private corpus path for local evaluation, while never
  committing or logging its metadata, paths, or source payload.
- Document actual baseline results and the review process for later policy changes.

## Out of scope

- Changing normalization, scores, thresholds, candidate caps, contradictions,
  clustering, Unified merge, keeper ranking, or policy versions.
- Parsing real EPUB/PDF files in the corpus harness. Content extraction/comparison
  remains covered by focused parser/signature tests; this evaluator injects labeled
  provider-neutral content-comparison outcomes.
- Implementing PDF fingerprints, language detection, covers, online providers,
  embeddings, or local models.
- Using a personal library or committing a library snapshot/export.
- Creating automatic aliases from one library's metadata.
- Treating initial baseline numbers as universal acceptance targets.
- Writing production logs, caches, library state, or Calibre data during evaluation.

## Relevant requirements

- An executable Candidate group means the same work and language; editions,
  revisions, illustrations, or formatting may coexist.
- Published groups process unless skipped, so false-positive groups are especially
  important even though an external backup is assumed.
- Matching improvements require labeled calibration and holdout evidence, not only
  unit tests or developer-library observations.
- Candidate generation must remain indexed/bounded and expose cap/global-limit loss.
- Exact Metadata candidates bypass ordinary Expanded candidate caps.
- Evidence, contradictions, coverage, policy versions, and deterministic IDs remain
  explicit.
- Matching/group identity is separate from quality and generated-keeper correctness.
- Corpora and reports must contain no copyrighted prose, credentials, private paths,
  or personal-library exports.

## Existing implementation inspected

### Pipeline

- `ExactMetadataDuplicateDetector` publishes uncapped exact normalized title/author
  groups independently of Expanded candidates.
- `BookMatchingProfileFactory` maps catalog/assessment facts into bounded profiles.
- `BookCandidateGenerator` indexes author aliases, suppresses oversized ordinary
  buckets, requires independent work evidence, retains at most 20 mutual ordinary
  candidates per record, preserves anchors, and has an effective global pair ceiling
  of `min(configured maximum, 10 * record count)`.
- `BookCandidateDecisionPolicy` adds oracle-equivalent/high/ambiguous/different/
  unavailable content evidence and assigns Rejected/Weak/Strong/Anchor disposition.
- `WorkLanguageCandidateClusterer` merges compatible Anchor then Strong relations,
  checks complete-component author/language/series/content contradictions, and emits
  language-partitioned Expanded groups.
- `UnifiedCandidateMergePolicy` combines Exact Metadata and disjoint Expanded groups,
  prevents contradictory bridges, emits review findings, guarantees final
  disjointness, classifies evidence, and chooses a generated keeper.

### Tests and tools

- Domain tests cover representative normalization, candidate caps, anchors,
  contradictions, clustering, Unified merge, disjointness, and keeper selection.
- An opt-in 20,000-record synthetic benchmark records time/allocation/candidate
  counts, but it does not measure precision or recall.
- `Extract-LibrarySnapshotMatches.ps1` searches private snapshots by text and writes
  source paths/items. It is a diagnostic utility and is not suitable as a committed
  corpus generator.
- No versioned labeled corpus, evaluator, confusion matrix, holdout split, or baseline
  report currently exists.

## Proposed design

### Corpus location and packaging

Store committed data under:

```text
tests/CalibreLibraryCleaner.Domain.Tests/Matching/Corpus/
  matching-corpus.schema.md
  calibration.v1.json
  holdout.v1.json
  baseline.v1.json
  SOURCES.md
```

Embed the JSON files as test resources so tests do not depend on the repository
working directory or copy files into arbitrary output locations. Use only
`System.Text.Json`; add no serialization or statistics package.

An optional external corpus may be supplied through
`CALIBRE_MATCHING_CORPUS_PATH`. Ordinary automated tests ignore that variable unless
an explicitly categorized local-evaluation test is selected. External-corpus output
uses opaque case/record IDs only and is never written under the repository.

### Corpus schema

Each corpus document contains:

- schema and corpus version;
- split: `Calibration` or `Holdout`;
- source/provenance/license summary;
- ordered scenarios.

Each scenario contains:

- stable opaque scenario ID;
- tags from a fixed vocabulary;
- source/provenance reference;
- optional generation-template ID and version for generated variants;
- two or more record fixtures;
- optional oracle content comparisons for canonical record pairs; and
- review notes that explain the intended hard case without embedding book prose.

Each record fixture contains only bounded facts needed to construct current Domain
values:

- opaque record key and deterministic synthetic Calibre ID;
- title and author variants/author-sort;
- identifiers;
- publisher/date/year, series/index, expected language, and cover flag;
- formats with canonical label and optional synthetic fingerprint identity;
- optional synthetic assessment facts: status, score, analyzer/scoring versions,
  bounded findings, and a stable non-rooted expected relative path;
- optional EPUB profile facts: embedded title, embedded authors, embedded languages,
  and embedded strong identifiers. The evaluator directly constructs Domain
  `EpubAssessment` values with `opened = true`, `packageParsed = true`, `Full`
  coverage, `Metadata` available, no score cap, and constructor defaults for all
  non-profile features unless a keeper case explicitly supplies compatible facts;
- ground-truth work key and expected normalized language; and
- expected-group `acceptableKeeperRecordIds`, containing one or more member keys for
  each expected multi-record group.

Ground truth is closed-world inside one scenario:

- records with the same work key and expected language are a positive pair;
- the same work in a different language is a required negative group relation;
- different work keys are negative pairs;
- every pair is therefore labeled and every predicted relation can be scored.

If a genuinely uncertain pair is needed later, the schema must add an explicit
`Excluded` pair label/version rather than silently omitting it from metrics.

Oracle content comparison contains only `EquivalentText`, `HighSimilarity`,
`Ambiguous`, `Different`, or `Unavailable` plus the exact bounded
`CandidateContentComparison` fields: forward/reverse strict matches,
forward/reverse relaxed matches, matched region count, token-count ratio permille,
and shingle-similarity permille. It does not contain source text or signatures. The
oracle is supplied only if the current pipeline requests content for that retained
pair; it cannot rescue a pair that candidate generation missed.

Oracle entries are optional. A requested pair with no entry is passed to the current
decision policy without a comparison, which yields its normal `Unavailable` result
and records oracle availability as false. An explicit `Unavailable` entry represents
a reviewed unavailable result and records oracle availability as true. Entries for
pairs that do not request content are validated but not injected.

`ExactMetadataDuplicateDetector` and every other evaluated policy are public Domain
code. The evaluator calls them directly; it must not reimplement normalization,
exact grouping, or matching policy and needs no Application/Infrastructure reference.

### Schema evolution and bounds

- `matching-corpus/1.0` and `matching-evaluation/1.0` freeze when the first baseline
  is accepted.
- Incompatible fields, labels, or semantics create the next major schema version;
  compatible additive changes require a minor version and explicit absence/default
  semantics. Mixed corpus schema versions are rejected.
- Check external file length before JSON parsing. Initial bounds are 32 MiB per
  document, depth 32, 10,000 scenarios, 100,000 total records, 100 records per
  scenario, 512 UTF-16 characters per scalar string, 64 authors/identifiers/formats/
  embedded values per record, 32 tags per scenario, and 4,950 content comparisons
  per scenario. Do not silently raise these bounds.
- Validation failures expose only corpus/scenario/record opaque IDs and stable error
  codes. They never echo title, author, identifier, path, source payload, or raw JSON.

### Split and leakage rules

- No work key, source work, or derived variant family may occur in both calibration
  and holdout.
- Holdout is at least 30% of labeled work families and remains unchanged while a
  policy is tuned; any holdout correction requires corpus version bump, provenance
  note, and regenerated baseline.
- Generated perturbation templates record template ID/version in scenario provenance.
  They may be used in both splits only on distinct works/authors; validation rejects
  generated work/source family leakage and reports template reuse counts for review.
- Corpus order is irrelevant; evaluator reruns reversed scenario/record order and
  requires semantically identical counts, rational metrics, opaque ID lists, and
  diagnostics after canonical serialization.

### Initial coverage matrix

The initial corpus must contain at least 100 work families, 200 records, 100 positive
same-work/language pairs, and 250 definitive negative pairs, with both splits
represented. At least five scenarios cover each applicable category:

- exact metadata and punctuation/Unicode/whitespace variants;
- author comma order, initials, compatible expansions, and conflicting full names;
- title noise, subtitles, volume notation, and conversion-prefix damage;
- validated identifier anchors and identifier collisions/conflicts;
- same title/author but different works;
- same work in different known languages;
- missing/unknown language and OPF/catalog disagreement;
- series/index compatibility and conflict;
- edition/revised/illustrated/annotated/abridged wording;
- equivalent/high/ambiguous/different/unavailable content evidence;
- missing EPUB, PDF-only, and metadata-only records;
- broad author buckets, ordinary top-20 cap, and decisive anchors beyond the cap;
- multi-record chains and contradictory bridge attempts;
- format/assessment/metadata/identifier/cover keeper-ranking differences; and
- malformed/incomplete metadata that should remain unmatched or review-only.

Edition/revision/illustration/formatting variants in the same language are required
positive same-work/language pairs under ADR 0021 even when the current matcher
rejects them. For example, plain, revised, and illustrated records for one English
work create all three positive pair labels. If one remains isolated, both pairs
involving it are false negatives. The baseline must expose those failures rather
than changing ground truth to fit current policy.

### Evaluator pipeline

The evaluator is test infrastructure and calls the real public Domain policies. For
each scenario it:

1. constructs `CalibreBook` and compatible assessment facts;
2. detects Exact Metadata groups;
3. creates matching profiles;
4. generates cheap candidates with current limits;
5. records candidate/proposal/cap/limit metrics;
6. injects oracle content only for requested candidate pairs;
7. decides and clusters Expanded groups;
8. merges Unified groups with current assessment facts;
9. compares predicted groups/keeper to ground truth; and
10. emits canonical stage outcomes and failures by opaque IDs.

The evaluator must not duplicate production scoring, normalization, clustering, or
keeper logic. Test code may calculate metrics and classify where a ground-truth pair
fell out of the production pipeline.

### Metrics

For definitive pairs inside scenarios, derive TP/FP/FN/TN from whether both records
appear in the same Unified group.

Report overall and per tag/split:

- pairwise precision, recall, and F1;
- TP/FP/FN/TN counts (ratios never replace counts);
- exact-component rate;
- overmerged predicted groups and fragmented expected groups;
- cross-language merge count;
- candidate-route recall: positive pairs reached by Exact Metadata or cheap Expanded
  candidate generation;
- Expanded candidate recall for positives not covered by Exact Metadata;
- content-request coverage and oracle availability;
- decision disposition counts;
- cap loss, oversized-bucket loss, global-limit outcomes, proposed/retained pairs;
- language-partition accuracy;
- Unified review-finding/classification counts;
- generated-keeper accuracy against acceptable keeper sets; and
- evaluation elapsed time and managed allocation as observations only.

Use integer counts and rational numerator/denominator in canonical JSON. Decimal
percentages are presentation fields rounded deterministically and are not used for
baseline equality. A zero denominator remains `{ numerator: 0, denominator: 0 }`
with an explicitly serialized `null` presentation percentage; it is never omitted
or coerced to zero or one.

An overmerge is one predicted group intersecting more than one expected group. A
fragmentation is one expected multi-record group intersecting more than one predicted
component, counting an unmatched member as a singleton component. Exact-component
rate counts expected multi-record groups whose member set equals one predicted group.

### Actionable failure classification

Every false negative is assigned to the first failed stage:

- `PROFILE_INELIGIBLE`;
- `AUTHOR_BUCKET_SUPPRESSED`;
- `CANDIDATE_NOT_PROPOSED`;
- `ORDINARY_CAP_DROPPED`;
- `GLOBAL_LIMIT_ABORTED`;
- `CONTENT_NOT_REQUESTED`;
- `CONTENT_UNAVAILABLE_OR_WEAK`;
- `PAIR_REJECTED_CONTRADICTION`;
- `PAIR_REMAINED_WEAK`;
- `COMPONENT_COMPATIBILITY_BLOCKED`;
- `UNIFIED_MERGE_SPLIT`; or
- `UNKNOWN_PIPELINE_GAP` (must be zero; any occurrence fails evaluation with opaque
  scenario IDs until the evaluator classification is extended and reviewed).

Every false positive records the predicted Unified group ID, involved scenario IDs,
evidence codes, contradiction codes, and relevant merge findings using opaque IDs
only. Keeper errors record expected acceptable keepers and actual generated keeper.

### Baseline report

`baseline.v1.json` contains:

- evaluation schema version;
- SHA-256 digest of canonical calibration and holdout corpus bytes;
- exact/matching/unified/keeper policy version identities;
- aggregate and per-tag/split metrics;
- canonical lists of FP/FN/keeper-error opaque IDs and failure categories; and
- recorded runtime/SDK for observational timing/allocation only.

The regression test recomputes and compares all semantic counts/lists/version/digests.
Timing and allocation are printed but not exact-equality gates. Input ordering must
not change report semantics.

The baseline's holdout digest enforces freeze discipline: changing holdout bytes
without an explicit corpus-version and baseline update fails before metric comparison.

No test silently rewrites corpus or baseline files. Updating the baseline is an
explicit reviewed edit accompanied by the new report, corpus/policy reason, and
matching documentation change.

### Future change gate

This first plan records the current baseline; it does not fail because current
precision or recall is below a speculative target.

After the baseline is accepted, a matching-policy change must:

- report calibration and holdout deltas;
- introduce no cross-language merge or decisive-contradiction violation;
- identify every new/removed FP and FN by scenario ID;
- avoid reducing holdout precision or recall without an explicit documented product
  tradeoff; and
- include cold/warm/performance evidence when candidate work or cache behavior
  changes.

The gate may be refined after the initial error distribution is known. It cannot be
weakened by editing ground truth to match the algorithm.

## Files expected to change

- `tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj`
- new evaluator/loader/metric/report code under
  `tests/CalibreLibraryCleaner.Domain.Tests/Matching/Evaluation/`
- new corpus and baseline files under
  `tests/CalibreLibraryCleaner.Domain.Tests/Matching/Corpus/`
- new strict schema/metric/evaluator/baseline tests under Domain tests
- optional focused Application test proving profile-factory mapping if corpus fields
  expose an orchestration mapping not already covered in Domain tests
- `docs/duplicate-detection.md`
- `docs/test-strategy.md`
- `docs/roadmap.md`
- this execution plan

No production source file is expected to change. Domain.Tests continues to reference
Domain only; a project/architecture assertion prevents adding Application or
Infrastructure references for the evaluator. If evaluation cannot use the public
matching policies without modifying production, stop and update this plan rather
than adding an evaluation-only hook to production.

## Safety considerations

- Never read a personal Calibre library or application cache from an ordinary test.
- Committed corpus data must be synthetic/generated or public-domain with recorded
  provenance/license.
- Do not commit snapshots, absolute paths, credentials, provider payloads, book prose,
  or copyrighted ebook content.
- External private corpora remain outside source control; reports use opaque IDs and
  aggregate metrics only. Evaluation returns an in-memory report and writes only to
  test output by default. Any explicitly requested report path must be outside both
  the repository and corpus directory, use an isolated per-run directory and atomic
  write, and never contain raw metadata.
- Strict file-size and streaming depth/count/string limits apply to external corpus
  JSON before materializing large collections.
- Corpus parse/validation errors fail the evaluation; they are never interpreted as
  negative labels.
- Baseline generation cannot mutate production state, caches, logs, or Calibre data.
- Measurements must not trigger cleanup or weaken matching contradictions.

## Implementation steps

1. **Define the corpus contract.** Add bounded immutable test records, fixed tag/
   split/content vocabularies, strict JSON options, canonical ordering, and schema
   documentation.
2. **Validate labels and provenance.** Reject duplicate IDs, incomplete records,
   unknown tags, invalid identifiers/languages, pair/content references outside a
   scenario, missing acceptable keeper, split leakage, private paths, and unsupported
  schema versions. Enforce concrete document/count/depth/string bounds before full
  materialization and redact all validation values except opaque IDs/error codes.
3. **Implement the evaluator.** Construct current Domain inputs and call the real
   Exact/Profile/Candidate/Decision/Cluster/Unified/Keeper policies in order.
4. **Implement metric math.** Add independently tested confusion matrices, pair/group
   comparison, keeper accuracy, category aggregation, and zero-denominator handling.
5. **Implement stage diagnostics.** Trace each expected positive through exact/cheap/
   content/decision/component/unified stages and classify FP/FN causes.
6. **Implement canonical reports.** Include complete digests/policy versions, stable
   ordering, rational metrics, opaque failure IDs, and observational timings.
7. **Create the calibration seed.** Port representative generic cases from existing
   matcher tests, then add hand-reviewed generated variants to satisfy the coverage
   matrix without copying production test helper assumptions blindly.
8. **Create the holdout set.** Curate disjoint synthetic and public-domain work
   families with provenance. Freeze before examining policy-change proposals.
9. **Record the current baseline.** Run evaluator, review every FP/FN/keeper error,
   eliminate corpus/schema mistakes, and commit the reviewed canonical baseline.
10. **Add regression tests.** Require corpus validation, deterministic reverse-order
    reports, baseline semantic equality, and stable failure categorization.
11. **Document results.** Record actual baseline metrics, dominant error categories,
    corpus limitations, and the next matching-policy plan; update roadmap progress.
12. **Run complete verification and review.** Confirm no production code, personal
    data, or policy behavior changed.

## Tests

### Corpus loader and schema

- supported schema/split/tag values load;
- unknown/missing/duplicate values fail with a controlled explanation;
- all scenario record IDs and content pair references are canonical and valid;
- same work/language labels derive deterministically;
- at least one acceptable keeper exists per expected multi-record group;
- calibration/holdout work/source families are disjoint;
- corpus bounds reject excessive documents/scenarios/records/strings/identifiers/
  formats/comparisons/depth;
- committed corpus contains no rooted paths or prohibited source/content fields; and
- corpus canonical digest is stable across dictionary/property/input order.
- schema major/minor compatibility and mixed-version rejection are deterministic.

### Metrics

- known confusion matrices produce exact counts/rational precision/recall/F1;
- zero predicted/zero positive denominators are handled explicitly;
- overmerge, fragmentation, exact-component, language, keeper, and per-tag metrics
  match hand-calculated fixtures;
- one scenario cannot create cross-scenario pair comparisons; and
- aggregation preserves integer totals.

### Evaluator

- current public policies are invoked end to end;
- Exact Metadata route is counted independently from Expanded candidate route;
- oracle content is injected only for requested retained pairs;
- cap, bucket, limit, contradiction, weak-edge, component, and Unified split failures
  receive the correct first-stage category;
- FP diagnostics contain only opaque IDs and evidence/problem codes;
- reversed corpus/scenario/record/content order gives semantically identical report
  counts, ratios, IDs, and diagnostics after canonical serialization;
- generated keeper is checked against an acceptable set; and
- evaluation performs no filesystem/network/process/cache/mutation work beyond
  reading embedded corpus resources.
- the Domain test project retains no Application/Infrastructure reference.

### Baseline

- calibration and holdout meet the coverage matrix;
- corpus/policy digests match the baseline;
- all semantic counts/metrics/failure lists equal the reviewed baseline;
- `UNKNOWN_PIPELINE_GAP` is zero or the test fails with opaque scenario IDs;
- observational timing/allocation is emitted but not equality-gated; and
- any baseline drift fails with a compact list of changed metrics/scenario IDs.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj --no-build
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Run the existing opt-in scale baseline separately and record it beside corpus
results; do not combine performance and quality assertions:

```powershell
$env:CALIBRE_RUN_MATCHING_BENCHMARK = '1'
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj `
  --no-build `
  --filter FullyQualifiedName~MatchingScaleBaselineTests `
  --logger "console;verbosity=detailed"
Remove-Item Env:CALIBRE_RUN_MATCHING_BENCHMARK
```

## Risks

- Synthetic variants can make the evaluator look better than real metadata. Include
  independent public-domain holdout families and disclose coverage limits.
- Ground-truth same-work decisions can be subjective for abridgements/adaptations.
  Apply ADR 0021 consistently, record scenario notes, and review disputed labels
  before freezing holdout.
- Pairwise metrics can hide component overmerge/fragmentation; report both pair and
  group metrics.
- A small holdout produces unstable percentages; always publish counts and avoid
  universal claims.
- Tests that mirror implementation logic can preserve bugs. Keep label derivation and
  metric math independent; call production matching policies rather than duplicating
  them.
- Baseline exact equality can discourage improvements. Require explicit reviewed
  updates rather than making the golden self-updating.
- Public-domain source metadata may still have attribution/licensing requirements;
  record provenance and include only necessary bounded bibliographic facts.
- External private-corpus support can leak metadata through assertion output. Use
  opaque IDs and aggregate output only.

## Unresolved questions

- Select the specific public-domain bibliographic source(s) during corpus curation.
  A source must allow repository redistribution of the bounded metadata used and must
  have stable provenance. This does not block schema/evaluator implementation.
- The initial baseline will reveal whether later gates need confidence intervals or
  category-weighted objectives; do not choose those before observing counts.

## Progress

- [x] Current matching pipeline, tests, scale benchmark, and diagnostic script
  inspected.
- [x] Corpus schema, split discipline, metrics, evaluator stages, and baseline update
  policy designed.
- [x] Execution plan written.
- [x] Corpus contract and strict loader implemented.
- [x] Metric and stage-diagnostic evaluator implemented.
- [x] Calibration corpus completed.
- [x] Holdout corpus completed and frozen.
- [x] Current baseline recorded and reviewed.
- [x] Full standard verification completed with 646/646 tests passing; existing
  scale evidence remains the performance baseline because production policy did not
  change.
- [x] Actual results documented and next policy slice selected.

## Final outcome

Implemented without changing matching policy or production behavior. The strict
embedded corpus expands to 62 scenarios, 474 records, 232 positive pairs, and 2,600
negative pairs, including independent minimal CC0 Wikidata families. The current
baseline is 91.3043% pair precision, 90.5172% recall, 90.9091% F1, 95.6897%
candidate-route recall, 89.6226% exact-component/keeper coverage, and 100% keeper
accuracy on complete predicted groups. It records 20 false positives and 22 false negatives with
no unknown pipeline gaps. The next slice may tune deterministic matching against
calibration but must demonstrate the frozen-holdout delta.

The public generator exposes proposed/retained counts and maximum bucket size, not
the exact members suppressed by an oversized bucket. The report therefore derives
ordinary directed cap loss, records scenarios crossing the current bucket threshold,
and leaves exact oversized-pair loss to focused generator tests. No evaluation-only
production hook was added.
