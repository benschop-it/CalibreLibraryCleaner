# EPUB unassessed technical failures

## Objective

Reserve EPUB `Disqualified` status for definitive file open/read failures. Represent incomplete technical inspection as `Unassessed` without a numeric score or any claim that Calibre cannot read the book.

## Scope

- Shared assessment status and invariants.
- EPUB problem-to-status and finding-severity policy.
- WPF status and score presentation.
- EPUB recommendation behavior for incomparable/unassessed candidates.
- Scoring-model version and governing documentation.

## Out of scope

- Claiming an EPUB is readable without opening it in Calibre.
- Assigning a quality score when mandatory facts are unavailable.
- Relaxing filesystem, archive, XML, resource, encryption, identity, extraction, or network safeguards.
- Changing PDF assessment policy.

## Relevant requirements

- Malformed EPUBs become findings rather than crashes.
- Scores and recommendations are explainable and comparable.
- Safety checks must not be presented as proof of document corruption.
- Scoring/disqualification semantic changes require a scoring-model bump.

## Existing implementation inspected

- `AssessmentStatus` had only `Completed` and `Disqualified`.
- Every fatal EPUB inspection problem became a disqualifying finding, including parser limits, unsupported features, encryption, and archive policy failures.
- Recommendations prefer completed EPUBs over decisively disqualified alternatives, so reusing `Disqualified` for analyzer limitations could influence retention.
- WPF rendered every null score as `Not scored — disqualified`.

## Proposed design

Add `AssessmentStatus.Unassessed`. It requires a null score, only zero-point non-disqualifying findings, and cannot participate in EPUB score comparison. `CannotOpen` and `Unreadable` remain disqualifying. Unsafe archive policy, malformed package, unsupported feature, encryption, changed-file, and resource-limit outcomes become warning-only `Unassessed` results. WPF states that the file may still open in Calibre. Recommendations treat unassessed alternatives as incomparable and require manual review; a sole unassessed EPUB is retained with an explicit warning.

## Files expected to change

- shared Domain assessment status/invariants and recommendation policy
- EPUB Application assessment engine
- EPUB WPF row/status presentation
- focused Domain, Application, recommendation, and WPF tests
- functional requirements, domain model, ADR 0004, and Milestone 4 plan

## Safety considerations

- No technical failure is converted into a positive readability claim.
- Unassessed EPUBs receive no score and cannot be preferred by quality comparison.
- Existing safety checks still stop unsafe parsing, extraction, resolution, or network access.
- Definitive missing/inaccessible/unreadable/open failures remain disqualifying.

## Implementation steps

1. Add failing status, engine, UI, and recommendation regressions.
2. Add the shared `Unassessed` invariant.
3. Map incomplete EPUB inspections to warning-only unassessed results.
4. Update WPF and recommendation presentation/behavior.
5. Advance the scoring model, update documentation, and run full verification.

## Tests

- Package, archive-policy, encryption, unsupported, changed, and limit failures are unassessed warnings.
- Cannot-open and unreadable failures remain disqualified.
- Unassessed assessments reject scores, disqualifiers, and hidden score adjustments.
- WPF displays `Not scored — unassessed` and explains that Calibre may still open the book.
- Unassessed alternatives remain unresolved; a sole unassessed EPUB is retained with manual-review warning.

## Verification commands

```powershell
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check HEAD
```

## Risks

- `Unassessed` is not proof of readability; UI wording must retain that distinction.
- Persisted assessments with older scoring models require reassessment before comparison.

## Unresolved questions

- None.

## Progress

- [x] Inspected status consumers, recommendation policy, UI, contracts, and tests.
- [x] Added failing status/engine/UI regressions.
- [x] Implemented `Unassessed` semantics and recommendation safeguards.
- [x] Updated scoring-model version and documentation.
- [x] Completed full verification and diff review.

## Final outcome

Completed on 2026-08-02. EPUB `Disqualified` is now reserved for definitive `CannotOpen` and `Unreadable` outcomes. Package, archive-policy, encryption, changed-identity, unsupported-feature, and resource-limit blockers are warning-only `Unassessed` results with no score and no hidden score adjustments. WPF displays `Not scored — unassessed` and explicitly states that the publication may still open in Calibre. Recommendations treat unassessed EPUBs as incomparable; non-identical alternatives remain unresolved and a sole unassessed copy is retained with manual-review warning.

Analyzer `epub-inspector/1.0.3` is unchanged. Scoring model advanced to `epub-quality/1.0.2`, and recommendation model advanced to `consolidation-recommendation/1.0.3`. The complete solution build succeeded with zero warnings and errors. The full suite passed 539 tests with 2 expected opt-in real-Calibre tests skipped. Focused domain, engine, recommendation, and WPF tests passed. `dotnet format --verify-no-changes`, diagnostics, complete diff review, and an independent read-only review passed without blocking findings.