# EPUB fallback-readable scoring

## Objective

Give safely fallback-readable EPUBs an evidence-derived numeric score with detailed warning penalties and a final maximum score of 70. Keep fully assessed EPUBs uncapped, insufficient-evidence EPUBs unassessed, and definitive open/read failures disqualified.

## Scope

- Explicit score-ceiling and inspection-coverage contracts.
- Safe-subset archive inventory and bounded fallback content inspection.
- Stable warning reason codes, bounded evidence, and initial warning penalties.
- Snapshot/recommendation persistence and model versioning.
- Conservative recommendation behavior and WPF presentation.

## Out of scope

- Calling Calibre to prove readability.
- Repairing EPUB files or Calibre metadata.
- Reading excluded unsafe, duplicated, encrypted, oversized, unsupported, or suspicious entries.
- Bypassing global limits, corrupt central directories, cancellation, or file-identity checks.
- Changing PDF assessment policy.

## Relevant requirements

- Readability and end-user usefulness take priority over strict standards conformance.
- Every score and warning remains explainable.
- Safety checks, no extraction, and no network access remain mandatory.
- Scores that lack complete package evidence must not overstate confidence.

## Existing implementation inspected

- Fully assessed EPUBs use findings-derived `epub-quality/1.0.2` scores.
- Incomplete technical inspection is `Unassessed`; definitive cannot-open/read is `Disqualified`.
- Some package/navigation defects already continue as zero-point recoverable warnings.
- `FormatAssessment` currently derives scores only from clamped score components and has no explicit ceiling.
- Recommendations compare only completed compatible EPUB assessments and use `consolidation-recommendation/1.0.3`.

## Proposed design

Fallback readability requires at least one safely parsed local XHTML/HTML/SVG candidate with non-empty text, a resolvable accepted local image/object, or bounded renderable SVG content. Fallback uses only safe, unique, unencrypted accepted archive entries. It returns `Completed` with explicit `FallbackReadable` coverage, normal evidence-derived scoring, per-occurrence warning penalties with category caps, and a score ceiling applied after all adjustments:

```text
score = min(clamp(sum(finding adjustments), 0, 100), 70)
```

The score ceiling is an explicit assessment contract, not a synthetic penalty. Findings retain the actual warning penalties. A zero-point cap explanation records the uncapped score and ceiling. Unknown facets do not earn positive points or absence penalties. Any capped candidate forces manual review for non-identical EPUB recommendation comparisons.

## Files expected to change

- Domain assessment score/coverage values and EPUB feature summary.
- Application EPUB inspection contracts and scoring engine.
- Infrastructure EPUB inspector, snapshot serializer, and recommendation serializer.
- Domain recommendation policy/model version.
- WPF EPUB assessment presentation.
- Focused Domain, Application, Infrastructure, recommendation, serialization, and WPF tests.
- Functional requirements, domain model, quality scoring, ADR 0004, and Milestone 4/5 plans.

## Safety considerations

- Global archive/file limits and corrupt central directories remain unassessed blockers.
- Every unsafe canonical-name collision member is excluded.
- Fallback never opens encrypted or otherwise excluded entries.
- Reads remain bounded, cancellation-aware, local, and read-only.
- Evidence/logging excludes content, credentials, raw exception messages, unsafe raw names, and external absolute paths.
- File identity is revalidated before publishing fallback facts.

## Implementation steps

1. Add explicit score-ceiling and coverage contracts with invariants.
2. Add stable issue reason codes and bounded structured evidence.
3. Refactor preflight into a deterministic safe archive inventory.
4. Add bounded fallback XHTML/HTML/SVG renderability inspection.
5. Add facet-aware scoring, warning penalties, and the 70 ceiling.
6. Persist coverage/ceiling/evidence and update recommendation behavior.
7. Update WPF, versions, and authoritative documentation.
8. Run focused and complete verification plus manual WPF acceptance.

## Initial warning penalties

- Invalid manifest item path/name: -3 each, cap -12.
- Malformed optional navigation or missing NCX navMap: -6 once.
- Missing/malformed mandatory container or package recovered by fallback: -12 each, combined cap -18.
- Unsupported parser/package behavior recovered by fallback: -10 once.
- Unsafe archive entry skipped: -4 each, cap -12.
- Duplicate canonical collision group skipped: -5 per group, cap -15.
- Encrypted entry skipped: -10 each, cap -20.
- Unsupported/suspicious/oversized entry skipped: -8 each, cap -16.
- No trustworthy reading order/package intent: -8 once.
- Other partial coverage: -5 once unless a specific warning applies.

## Tests

- Fully assessed warning-heavy EPUB remains uncapped.
- Fallback-readable raw score above 70 stores 70 and exposes its uncapped score.
- Fallback-readable raw score below 70 remains below 70 but still exposes the ceiling.
- No accepted renderable content remains unassessed.
- Cannot-open/read remains disqualified.
- Safe-subset fallback never reads excluded entries.
- Warning penalties apply per occurrence up to category caps; excess evidence remains visible at zero adjustment.
- Capped candidates force manual review except exact-binary and sole-copy retention.
- Snapshot/recommendation round trips preserve ceiling, coverage, and evidence.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check HEAD
```

## Risks

- Bounded local renderability is not proof that Calibre opened the book or that reading order is correct.
- Broad extension-based fallback candidates can include auxiliary documents; canonical ordering and the 70 ceiling limit confidence.
- Old persisted assessments require a fresh scan before comparison with new model versions.

## Unresolved questions

- None. Approved policy: renderable local content threshold, safe-subset fallback, per-occurrence capped penalties, score ceiling 70, and manual review for capped non-identical candidates.

## Progress

- [x] Inspected current implementation and approved policy boundaries.
- [x] Added score-ceiling and coverage contracts.
- [x] Added safe archive inventory and fallback reader.
- [x] Added warning penalties and capped scoring.
- [x] Updated persistence, recommendations, WPF, versions, and docs.
- [x] Completed verification and diff review.

## Final outcome

Implemented fallback-readable EPUB scoring with safe-subset archive inspection, structured issue/facet evidence, per-occurrence warning penalties, and an explicit score ceiling of 70. Unknown facets do not receive positive or absence points. Protected, unsafe, duplicated, unsupported, suspicious, oversized, and ZIP-encrypted entries are excluded; unreadable encryption metadata, corrupt/global archive limits, cancellation, and changed file identity remain blockers. Snapshot round trips preserve coverage, renderability evidence, uncapped score derivation, and the ceiling, with malformed persisted combinations rejected.

Non-identical recommendation comparisons involving a capped EPUB now remain unresolved and require manual review. Sole capped copies are retained with a review warning, while exact-binary selection remains allowed. WPF displays coverage and numeric capped scores such as `65 (fallback; max 70)` with uncapped score and renderability details.

Final verification succeeded: `dotnet restore`, `dotnet build --no-restore`, `dotnet test --no-build` (563 passed, 2 expected opt-in real-Calibre tests skipped), `dotnet format --verify-no-changes`, `dotnet list package --vulnerable --include-transitive` (no known vulnerable packages), and `git diff --check HEAD`.