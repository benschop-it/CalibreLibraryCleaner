# Edition-Neutral Title Candidate Recall

## Objective

Recover the ten labeled same-work/language pairs that current candidate generation
never proposes, without reducing the committed 100% pair precision or weakening
language, identifier, series, content, cap, or component safeguards.

## Scope

- Make cheap title-token similarity neutral to the established bounded edition-marker
  vocabulary: `ABRIDGED`, `ANNOTATED`, `EXPANDED`, `ILLUSTRATED`, `REVISED`, and
  `UNABRIDGED`.
- Keep original title keys/tokens in profiles and reports; only the Jaccard similarity
  input excludes edition markers.
- Preserve the existing 500-permille work-evidence threshold and 450 candidate score.
- Centralize the edition-marker predicate in `CandidateMetadataNormalizer` so
  candidate generation and Unified compatibility use one vocabulary.
- Bump `MatchingPolicyVersion` and Candidate analysis workflow policy identity.
- Measure calibration and frozen-holdout deltas, review changed opaque FP/FN IDs,
  update the semantic baseline, and reconcile docs.

## Out of scope

- Lowering candidate score/work-evidence thresholds.
- Adding synonyms, stemming, translation dictionaries, edit-distance/fuzzy string
  algorithms, provider data, local models, PDF evidence, or library-specific aliases.
- Changing author normalization, candidate indexes/caps, content comparison,
  decision thresholds, clustering, Exact Metadata, Unified grouping, or keeper policy.
- Addressing the remaining unavailable/weak-content false negatives in this slice.
- Changing corpus labels or the frozen holdout.

## Relevant requirements

- Candidate generation remains indexed and bounded; same author alone never proposes
  a work.
- Different editions/revisions may belong to one same-work/language Candidate group.
- Decisive contradictions remain authoritative.
- Matching changes require calibration and frozen-holdout evidence with stable opaque
  failure IDs.
- Persisted Candidate analysis from an older policy must not remain mutation-authoritative.
- No personal-library observations or aliases enter production policy.

## Existing implementation inspected

- All ten `CANDIDATE_NOT_PROPOSED` false negatives are five generated calibration and
  five generated holdout instances of one pattern:
  `Gardens Beneath Glass`/`Gardens Under Glass Revised` or
  `Bridges Across Mist`/`Bridges Through Mist Revised`.
- These pairs share compatible authors and two stable title tokens. Their raw token
  Jaccard score is 400 because one connector changes and `REVISED` is added, below
  the existing 500 work-evidence threshold.
- Removing only `REVISED` yields 500, so the existing score/threshold requests the
  already-labeled `HighSimilarity` content oracle.
- Same-author different-work tests already prove author identity alone cannot propose
  a candidate.
- Unified merge independently recognizes the same six edition markers.
- `MatchingPolicyVersion.Current` is `work-language-matching/1.2.0`; workflow
  Candidate analysis is `candidate-analysis/1.0.0`.
- Current baseline is 100% precision, 90.5172% recall, 95.6897% candidate-route
  recall, 22 false negatives, and no false positives.

## Proposed design

### Similarity normalization

Add `CandidateMetadataNormalizer.IsEditionMarker(string)` over one fixed ordinal set.
In `BookCandidateGenerator.Score`, derive bounded comparison token arrays by filtering
`TitleTokens` through that predicate. Compute title Jaccard from those arrays; do not
mutate profiles or title keys.

If filtering makes either title empty, similarity remains zero. Edition-marker overlap
alone therefore cannot create work evidence. Evidence retains the existing
`MATCH.TITLE.TOKEN_*` bands because the algorithm remains token Jaccard, now over the
work-bearing token subset.

Update Unified edition-marker comparison to call the shared predicate. No Unified
behavior changes.

### Versioning

- Add `MatchingPolicyVersion.V4 = work-language-matching/1.3.0` and make it current.
- Change workflow Candidate analysis identity to `candidate-analysis/1.1.0`.
- Existing state migration must reduce stale policy state to
  `RequiresExactAnalysis`; no snapshot schema change is needed.
- Baseline policy provenance updates through existing evaluator wiring.

### Acceptance gate

Calibration and holdout must each:

- retain 100% pair precision and zero false positives;
- improve candidate-route recall from 95.6897% to 100%;
- improve pair recall above 90.5172%;
- contain no overmerge, cross-language merge, or unknown pipeline gap; and
- show only the expected `b1`/`b2` generated-scenario pairs removed from the false-
  negative set.

The remaining 12 unavailable/weak-content false negatives are explicitly unchanged.

## Files expected to change

- `src/CalibreLibraryCleaner.Domain/Matching/BookMatchingProfile.cs`
- `src/CalibreLibraryCleaner.Domain/Matching/BookCandidateGenerator.cs`
- `src/CalibreLibraryCleaner.Domain/Matching/UnifiedCandidateGroup.cs`
- `src/CalibreLibraryCleaner.Domain/Matching/WorkLanguageCandidateGroup.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/LibraryState.cs`
- focused Domain and state persistence tests
- matching evaluator quality gate and reviewed baseline
- `docs/duplicate-detection.md`, `docs/test-strategy.md`, `docs/roadmap.md`
- this plan and `docs/plans/README.md`

No Application, Infrastructure, or WPF production source change is expected.

## Safety considerations

- The change can only propose additional read-only Candidate pairs; content and
  contradiction policies still decide grouping.
- Edition-marker-only overlap is ignored rather than promoted.
- Existing candidate caps/global ceiling remain unchanged.
- The frozen holdout and 100% precision gate constrain overgeneration.
- Workflow policy versioning prevents stale Candidate analysis from retaining
  mutation authority.
- Reports remain aggregate/opaque and contain no private metadata.

## Implementation steps

1. Add focused generator tests for revised-title recovery and marker-only rejection.
2. Centralize the edition-marker predicate and apply it only to title similarity.
3. Bump matching and Candidate workflow policy versions with migration coverage.
4. Run focused Domain/state tests.
5. Emit a temporary report and review split metrics plus changed FP/FN IDs.
6. Publish the reviewed semantic baseline only when all gates pass.
7. Reconcile docs, archive the plan, and run standard verification.

## Tests

- connector substitution plus `Revised` reaches a content-requesting candidate;
- the same case without a recognized marker retains current scoring behavior;
- shared edition-marker token alone cannot create a candidate;
- unrelated books by one author remain unproposed;
- language/series contradictions remain present and decisive;
- ordinary caps, anchor preservation, global limit, determinism, and 20,000-record
  scale behavior remain unchanged;
- current matching policy is `work-language-matching/1.3.0`;
- stale Candidate analysis workflow policy migrates conservatively;
- calibration/holdout precision remains 100% and candidate-route recall becomes 100%;
- baseline semantic equality passes after reviewed update.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj --no-build
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --no-build
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

## Risks

- Edition-marker removal can increase proposals among books by the same author. The
  unchanged work threshold, content requirement, contradictions, and frozen-holdout
  precision gate mitigate this.
- The marker vocabulary is English and incomplete. This slice intentionally avoids
  uncalibrated multilingual expansion.
- Centralizing marker vocabulary could accidentally change Unified behavior. A
  focused equality test and existing Unified suite guard against vocabulary drift.
- Added proposals may increase cap pressure in large author buckets. Existing cap and
  scale tests remain required; performance metrics are observational because the
  index and asymptotic work do not change.

## Unresolved questions

None block this slice. Broader subtitle/connector normalization requires separate
corpus evidence and is not inferred from these ten cases.

## Progress

- [x] Missed candidate pairs and controlling score path inspected.
- [x] Narrow edition-neutral similarity hypothesis selected.
- [x] Calibration/holdout acceptance gate defined.
- [x] Focused policy tests added.
- [x] Matching/workflow versions and implementation updated.
- [x] Calibration/holdout deltas reviewed.
- [x] Baseline and docs updated.
- [x] Standard verification completed with 657/657 tests passing.

## Final outcome

Implemented `work-language-matching/1.3.0` and `candidate-analysis/1.1.0`.
Candidate title Jaccard now excludes only the six shared edition markers while
retaining original profile/evidence tokens and all existing thresholds, indexes,
caps, contradictions, content decisions, and grouping policy. Unified edition-marker
compatibility uses the same bounded vocabulary. Stale Candidate workflow state
migrates conservatively to `RequiresExactAnalysis`.

Calibration and frozen holdout changed identically:

- precision: unchanged at 100%;
- recall: 90.5172% to 94.8276%;
- F1: 95.0226% to 97.3451%;
- candidate-route recall: 95.6897% to 100%;
- false positives: unchanged at 0;
- false negatives: 11 to 6 per split, 22 to 12 overall; and
- keeper coverage: 89.6226% to 94.3396%.

Exactly the five `b1`/`b2` generated title-variant pairs in each split left
`CANDIDATE_NOT_PROPOSED`; no new FP/FN ID appeared. All 12 remaining false negatives
are `CONTENT_UNAVAILABLE_OR_WEAK`. One unrelated controlled-viewer temporary-file
cleanup test failed transiently during the first full run, passed in isolation, and
the authoritative serialized rerun passed 657/657. The opt-in scale benchmark was
not rerun because indexing, limits, asymptotic work, and production I/O were
unchanged; ordinary cap/determinism tests and the full suite passed.
