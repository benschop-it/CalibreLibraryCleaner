# Conservative metadata review and merge preview

## Objective
Prevent automatic metadata proposals from replacing richer current values, prefer
normal display casing over all-caps candidates, and show the exact merged outcome.

## Scope
Domain field applicability/merge preview and casing quality; book-aware metadata
review defaults and persisted overrides; WPF filters/details; focused and standard
verification against the reported edition mismatch.

## Out of scope
Automatically rewriting case, deleting current covers/identifiers, changing provider
request behavior, direct library mutation, or adding a new workflow stage.

## Relevant requirements
Explainable review, explicit Apply for risky changes, preserve local-only fields,
merge identifiers, retain current cover, exact worker read-back and review convergence.

## Existing implementation inspected
EditionMetadataApplicationPolicy, proposal scoring/fusion, review reconciliation,
executor metadata operations, metadata review row/details UI, current catalog and
cached provider candidates for the reported title.

## Proposed design
Keep proposals complete but default Apply only when every actual change is additive
or a bounded casing improvement. Existing populated edition fields and same-type
identifiers cannot be replaced automatically. Penalize all-caps candidate titles and
authors during versioned fusion, without inventing capitalization or invalidating
provider caches. Persist explicit Apply against the book-aware default. Expose one
Domain merge preview and render it as a third Final merged metadata column using the
current checkbox state.

## Files expected to change
Domain application/review/fusion policies and tests; Application preparation
reconciliation and tests; WPF row/XAML/summary/tests; current docs and this plan.

## Safety considerations
Current Google identifiers and covers remain preserved. Empty/unavailable optional
fields remain non-applicable. Explicit overrides remain possible but require review.
No library writes or live cache changes in tests; no ordinary metadata is logged.
The fixed worker and exact read-back remain the only mutation authority.

## Implementation steps
1. Add deterministic merge preview, casing quality, and conservative default policy.
2. Reconcile defaults/overrides and update filters/summaries.
3. Add the Final merged metadata UI column and verify execution parity.
4. Run focused and standard gates; record the reported example outcome.

## Tests
All-caps fusion ranking; additive versus destructive defaults; author subset,
same-type identifier replacement, populated optional fields, unavailable values,
identifier merge, cover preservation, checkbox-dependent final preview, persisted
explicit Apply/reset, filter counts, WPF bindings/window load, executor field parity,
decision privacy/versioning and full regression coverage.

## Verification commands
All standard gates passed on 2026-10-16:

- `dotnet restore`
- `dotnet build --no-restore`
- `dotnet test --no-build --maxcpucount:1`: 1,076 passed, zero failed or skipped.
- `dotnet format --verify-no-changes`
- `git diff --check`
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

Focused results: 31 Domain metadata cases, 7 Application review cases, 7 WPF
row/window cases and 2 decision-store cases passed.

## Risks
Conservative defaults increase manual review volume by design. Explicit Apply can
still select a poorer edition, but the Final merged metadata column exposes the exact
result first. All-caps preference is a penalty, not automatic case conversion.
Existing saved review decisions under fusion policy 1.0 do not restore under 1.1.
No destructive real-library acceptance was performed.

## Unresolved questions
None for this approved conservative behavior.

## Progress
The reported example was inspected read-only. Current metadata has two authors, a
Google identifier, ISBN, publisher/date, Dutch language and a cover. Primary-author
fallback returned an all-caps, one-author Open Library candidate from another edition.
Identifiers and cover were already protected, but confidence alone incorrectly
defaulted the remaining replacements checked.

The central merge preview, conservative automatic policy, persisted default/override
state, true automatic/review filters, summary counts, and third WPF details column
were implemented. Fusion policy 1.1 applies the all-caps penalty over cached provider
candidates; sole all-caps candidates remain available and review-gated when risky.
The first override test exposed loss of the book-aware baseline after checking Apply;
retaining RequiresReview across the override fixed reset semantics. The first WPF
test used a fixture without the Google ID asserted by the new preview; correcting the
synthetic current record proved identifier preservation. The first full suite exposed
one decision-store assertion pinned to fusion 1.0; updating it to intentional 1.1
restored the full suite. All gates passed after the user closed the locking app.

## Final outcome
Completed. Automatic Apply is now merge-quality-aware rather than confidence-only.
Risky High/Medium proposals are unchecked under Needs review. Missing proposal fields
never clear populated values; identifiers merge and existing cover wins. Mixed-case
provider candidates are preferred over comparable all-caps candidates. Selected
proposal details now show Current, Proposed and Final merged metadata.