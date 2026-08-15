# Contradiction-Aware Exact Metadata

## Objective

Prevent executable Exact Metadata and Unified Candidate groups from joining records
with decisive known catalog-language or validated strong-identifier conflicts. Keep
Exact title/author normalization and its uncapped routing role, then measure the
precision/recall delta against the committed calibration and frozen holdout corpus.

## Scope

- Add a versioned Domain policy for publishable Exact Metadata groups.
- Within each exact normalized title/author bucket, evaluate normalized catalog
  languages and validated ISBN/DOI/ASIN/OCLC values before group publication.
- Remove disjoint outliers only when one repeated signal consensus exists.
- Suppress an identity bucket when conflicting known values have no unique repeated
  consensus or fewer than two compatible members remain.
- Preserve unknown/missing signal members with the compatible consensus; absence is
  not a contradiction.
- Apply the behavior at `ExactMetadataDuplicateDetector`, covering initial Exact
  analysis, residual Candidate analysis, projected-state recomputation, and Unified
  merge inputs without separate call-site filtering.
- Bump Exact analysis workflow and evaluator policy identities.
- Record calibration and holdout deltas, update the reviewed semantic baseline, and
  reconcile matching documentation.

## Out of scope

- Changing normalized title/author identity, candidate scores, ordinary caps,
  content thresholds, clustering, keeper ranking, or mutation behavior.
- Using EPUB embedded languages/identifiers or content comparison during Exact-first
  analysis; those facts are not uniformly available at that stage.
- Splitting one exact identity into multiple published groups. Exact group IDs are
  identity-based, so multiple groups would collide without a separate ID/schema
  migration.
- Treating missing languages/identifiers as conflicts.
- Adding provider, PDF, cover, embedding, or cache evidence.
- Tuning ground truth or changing the frozen holdout corpus.

## Relevant requirements

- An executable group means the same work and normalized language.
- Published groups process unless skipped, so known decisive contradictions must not
  rely only on later recommendation warnings.
- Exact Metadata remains uncapped and independent of Expanded ordinary candidate
  limits.
- Matching changes require calibration and frozen-holdout deltas with stable opaque
  failure IDs.
- Existing persisted workflow state with an old Exact analysis policy must require a
  new analysis before mutation.
- No direct Calibre/database/filesystem mutation is introduced.

## Existing implementation inspected

- `ExactMetadataDuplicateDetector` groups all records sharing one normalized title
  and complete author set and publishes one identity-based group ID.
- `ScanLibraryUseCase`, `AnalyzeResidualCandidatesUseCase`, and projected metadata
  deltas all call the detector directly.
- Exact groups are executable before Candidate content analysis and are also supplied
  to `UnifiedCandidateMergePolicy`.
- Unified merge already blocks many contradictory Expanded-component bridges, but a
  metadata-only Exact group remains publishable.
- `ConsolidationRecommendationPolicy` detects disjoint language/identifier signals
  later and uses a unique repeated-consensus/outlier rule; this is too late to define
  executable group identity.
- `LibraryWorkflowPolicyVersions` invalidates authoritative persisted state when
  policy identities change. A legacy read-only snapshot migrates to
  `RequiresExactAnalysis`, so no snapshot schema change is needed.
- Baseline `matching-evaluation/1.0` records 20 false positives: ten from conflicting
  validated identifiers and ten from known language conflicts.

## Proposed design

### Exact conflict policy

Introduce `ExactMetadataPolicyVersion` with current value
`exact-metadata/1.1.0`. `ExactMetadataDuplicateDetector.PolicyVersion` exposes it for
workflow/evaluation provenance.

For every normalized identity bucket:

1. order distinct records by Calibre ID;
2. derive each record's non-empty normalized catalog-language set using the current
   generic language normalizer;
3. for each supported strong identifier type, derive validated normalized values
   using the current generic strong-identifier normalizer;
4. if all known sets for a signal overlap pairwise, retain all members;
5. if disjoint sets exist and exactly one identical signal signature occurs at least
   twice, exclude known members disjoint from that consensus while retaining unknown
   members;
6. if no unique repeated consensus exists, suppress the whole identity bucket;
7. union outliers across signals and publish the remaining bucket only when at least
   two records remain.

This is deterministic and conservative. It never creates two groups with the same
identity ID. It preserves ordinary exact pairs and a clear majority plus unknown
members, while ambiguous two-way/tied conflicts become non-executable.

### Versioning and persistence

- Change workflow Exact analysis identity from `exact-analysis/1.0.0` to
  `exact-analysis/1.1.0`.
- Use `ExactMetadataDuplicateDetector.PolicyVersion` in the matching evaluation
  report instead of a test-owned literal.
- Do not change snapshot JSON schema or Exact group ID version: new scans recompute
  groups, authoritative state rejects stale workflow versions, and loaded legacy
  snapshots require Exact analysis before cleanup.

### Evaluation gate

Run calibration and holdout separately, then the combined semantic regression:

- expect all 20 current false positives to disappear;
- require no new false positive, cross-language merge, or unknown pipeline gap;
- require calibration and holdout precision to improve;
- require holdout recall and candidate-route recall not to decrease;
- review every changed opaque FP/FN ID before replacing `baseline.v1.json`.

## Files expected to change

- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactMetadataDuplicateDetector.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/LibraryState.cs`
- detector/workflow/evaluator tests under Domain tests
- matching corpus evaluator policy identity wiring and reviewed baseline
- `docs/duplicate-detection.md`
- `docs/test-strategy.md`
- `docs/roadmap.md`
- this plan and `docs/plans/README.md`

No Application, Infrastructure, or WPF production source change is expected.

## Safety considerations

- Only catalog metadata already loaded read-only is inspected.
- Invalid identifiers and unknown languages provide no contradiction.
- Suppression produces fewer executable groups; it never broadens mutation authority.
- Old authoritative state cannot remain current after the Exact analysis policy bump.
- Reports retain opaque IDs and aggregate metrics only.
- The frozen holdout corpus and labels are not modified.

## Implementation steps

1. Add policy version and focused detector conflict tests.
2. Implement deterministic unique-consensus filtering inside the detector.
3. Bump workflow/evaluator policy identities and add stale-version coverage.
4. Run focused Domain and state tests.
5. Emit calibration/holdout deltas and review every changed FP/FN.
6. Update the reviewed baseline only after gates pass.
7. Reconcile authoritative docs, archive this plan, and run standard verification.

## Tests

- two known disjoint languages suppress a two-record exact bucket;
- language aliases such as `eng`/`en` remain compatible;
- missing language does not contradict one known consensus;
- two conflicting validated identifiers of the same type suppress a bucket;
- invalid identifiers do not affect grouping;
- overlapping multi-value identifier sets remain compatible;
- one repeated consensus excludes one disjoint outlier;
- tied repeated consensuses suppress the bucket rather than create duplicate IDs;
- outliers from language and identifier signals are unioned deterministically;
- shuffled input produces identical groups/IDs;
- 50,000-record scale behavior remains bounded;
- old workflow policy versions are not current;
- corpus precision improves with no holdout recall/candidate-route regression;
- semantic baseline equality passes after reviewed update.

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

- ISBNs identify editions, so disjoint valid ISBNs can separate editions of the same
  work. The accepted current policy already treats same-type strong-identifier
  conflicts as decisive; corpus/holdout metrics and conservative consensus handling
  bound this risk.
- Suppressing tied conflict clusters loses recall. Publishing both would require a
  member-based Exact group ID and persistence migration, which is intentionally
  deferred.
- Catalog language can be wrong. Unknown values remain neutral and a repeated
  consensus can retain compatible members while excluding one disjoint outlier.
- Exact policy changes alter persisted workflow authority; forgetting the version
  bump could allow stale executable groups.

## Unresolved questions

None block this slice. A later plan may introduce member-based Exact group IDs if
measured tied-cluster recall justifies the migration.

## Progress

- [x] Executable Exact and Unified paths inspected.
- [x] Baseline false-positive categories identified.
- [x] Conflict and persistence design selected.
- [x] Focused policy tests added.
- [x] Detector and policy versions implemented.
- [x] Calibration/holdout deltas reviewed.
- [x] Baseline and documentation updated.
- [x] Standard verification completed with 654/654 tests passing.

## Final outcome

Implemented `exact-metadata/1.1.0` and `exact-analysis/1.1.0`. Exact normalized
title/author buckets now remain executable only when known normalized languages and
validated same-type strong identifiers are compatible or one repeated consensus can
exclude disjoint outliers. Ambiguous/tied conflicts are suppressed; unknown values
remain neutral. Existing Unified, recommendation, Application override, and WPF
defensive behavior remains tested through explicit raw-group fixtures.

Calibration and frozen holdout changed identically:

- precision: 91.3043% to 100%;
- recall: unchanged at 90.5172%;
- F1: 90.9091% to 95.0226%;
- candidate-route recall: unchanged at 95.6897%;
- false positives: 10 to 0 per split, 20 to 0 overall;
- false negatives: unchanged at 11 per split, 22 overall; and
- overmerged/cross-language groups: 0 overall.

The architecture source rule was amended to permit deterministic validated strong
identifiers in Exact Metadata while continuing to prohibit integration and fuzzy
algorithms. The reviewed semantic baseline and authoritative docs now make the 22
false negatives the next Priority 1 work.
