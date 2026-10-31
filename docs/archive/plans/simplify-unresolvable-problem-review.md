# Simplify Unresolvable problem review

## Objective

Show a comprehensible list of actual suspected metadata/content problems instead
of presenting incomplete verification as bad books. Make move selection predictable.

## Scope

Conservative review-only catalog problem detection, plain-language explanations and
next actions, one optional show-all control, real check-all-shown behavior and
removal of read-only status checkboxes. Keep full final accounting and evidence.

## Out of scope

Changing duplicate matching, automatically qualifying plausible metadata, mass
automatic quarantine for missing evidence, changing mutation/backup/worker safety,
cache invalidation or accessing/mutating the user's library for tests.

## Relevant requirements

- Current user request: show serious problems such as author text in a title,
  title-like author text and bracketed numeric author annotations.
- [Functional requirements](../../functional-requirements.md): explicit reviewed
  transfer, complete accounting and missing evidence never implying corruption.
- [Safety](../../safety-and-rollback.md): fixed supported mutation and backup confirmation.
- [ADR 0021](../../adr/0021-matching-first-performance-oriented-product.md): explainability
  and simple review, without altering matching.

## Existing implementation inspected

NeedsReview combines missing identity/content/naming evidence with actual damage.
The UI shows raw enums/reason codes and read-only Manual status as checkboxes.
Check all is enabled for unchecked NeedsReview rows but its Application helper
intentionally selects only policy proposals or previously manual assignments.
Individual selection already permits explicit manual transfer. Full-state
qualification and missing-layout gates are separate from transfer selection.

## Proposed design

Retain internal qualification but do not use it as the primary user filter. Default
to concrete current-catalog problems, confirmed unusable content and checked moves.
Offer only "Show all books" for inspecting excluded normal-looking records.
Use bounded shared Domain triage/suspicion facts plus conservative review-only
checks for a complete author attached to a title by a separator and numeric
bracketed author annotations. Do not treat normal bibliographic mentions as damage.
These observations do not change matching, qualification or automatic move defaults.

Give each row plain-language problems, verification status and actionable guidance.
Put raw codes, coverage and provenance in an optional details expander. Remove
qualification and Manual columns and manual-ID controls from the primary toolbar;
keep deliberate existing ID selection in optional tools. Check all shown explicitly
selects exactly visible records through existing manual selection authority. Hidden
records are untouched. Move still requires confirmation and complete external backup.

## Files expected to change

Domain review-only policy/tests, Application subject evidence, WPF view-model/XAML,
command/visual regression tests and directly related current docs.

## Safety considerations

Hiding incomplete evidence must not qualify a book or drop it from accounting.
No new review signal auto-checks a move. Empty views disable check-all. Checking
rows is explicit transfer choice, never qualification. Repeated check/uncheck stays
deterministic and all writes remain in the fixed worker after confirmation.

## Implementation steps

1. Freeze labeled problem/counterexample cases and visible-selection regressions.
2. Implement bounded review-only facts and plain-language WPF presentation.
3. Replace confusing filters/status controls and wire visible bulk selection.
4. Verify qualification/accounting, actual WPF bindings, normal build and full suite.

## Tests

Author-in-title separators, title-like author, numeric brackets, missing/placeholder
metadata, damaged separators, multilingual names and legitimate counterexamples.
Plausible but unverified records are hidden by default, still counted and optionally
inspectable. Checkbox, selected and all-shown choices agree; no hidden selection.
Actual WPF grid has one editable Move checkbox and no Manual/status checkboxes or
seven-way filter. Verification-only rows explain absence of defects honestly.

## Verification commands

Focused Domain/Application/WPF tests, generated WPF visual/layout check, normal
Debug build, full test suite, format, whitespace and dependency vulnerability audit.
Never terminate the user's app or build alternate outputs to bypass locks.

## Risks

Title/author overlap may be legitimate. Require complete multi-token author credit
at a delimiter boundary and label it as a suspicion, not definite corruption.
Normal-looking unverified books remain pending internally; explain this without
claiming a fully certified library.

## Unresolved questions

None. Use an opt-in all-books view and keep verification/transfer authority separate.

## Progress

- [x] Traced the bulk-selection mismatch and read-only status checkbox.
- [x] Implemented bounded review-only metadata signals and explicit local
  reversed-field evidence without changing qualification, matching or move defaults.
- [x] Replaced seven filter choices with a default problem list and one Show all
  checkbox. Removed read-only Manual/qualification columns; technical evidence,
  optional certification and ID assignment remain in collapsed details.
- [x] Check all shown now selects exactly visible IDs through explicit reviewed
  assignment; per-row, selected and bulk choices agree. Empty/already-checked views
  disable the action. Missing proof does not qualify or move hidden books.
- [x] Domain labeled checks (25), focused Application checks (24) and focused WPF
  checks (13) passed. Actual checkbox click and generated visual capture passed.
- [x] Initial WPF verification was blocked by the running app and .NET startup hit
  system commit pressure. No processes were killed; after the app lock released,
  normal targeted compilation/tests succeeded with bounded build concurrency.
- [x] Normal Debug solution build passed with zero warnings/errors; full suite
  passed: Domain 517, Application 331, Infrastructure 387, Architecture 34,
  WPF 106; 1,375 total, zero skipped.
- [x] Subsequent final no-selection guidance passed all 106 WPF tests and full
  formatting verification. Whitespace and dependency vulnerability audit passed.
- [x] Inspected the generated-only rendered UI and actual editable checkbox click;
  removed the temporary preview. No personal-library mutation or automated test.
- [x] Current docs reconciled and execution plan archived.

## Final outcome

Completed a problem-focused Unresolvable UI. The primary view lists suspected
current-catalog damage, confirmed unusable content and checked moves; one Show all
checkbox reveals normal-looking verification-only records without labelling them
defective or automatically certifying them. Plain-language problem descriptions
and inspection instructions replace raw enum/reason-code columns. One Move checkbox
is editable; Manual/qualification status checkboxes and the seven-way filter are
gone. Technical evidence, full retained accounting and optional verification/ID
tools remain accessible in collapsed details.

Check all shown now does what its label says, explicitly selecting visible pending
problem records while leaving hidden books alone. No selection action mutates
Calibre files. Hidden records remain in qualified/pending/moved accounting.
Independent local credit evidence is required for a reversed-field warning;
the new review-only heuristics do not change matching, provider caches, naming
verification or automatic quarantine defaults. Explicitly accepted legitimate
warnings leave the default problem list but remain inspectable.

The latest application-owned manifest inspected read-only is authoritative,
Completed, revision 17,888 with no pending mutation. This UI-only change therefore
uses **F5 -> select the same library -> Load, not Scan**. Transient final review
workspaces/checkboxes are rebuilt using the existing enabled final-author/naming
steps and Prepare Unresolvable review; unconfirmed move selections must be chosen
again. No resumability/recovery feature or unsafe qualification shortcut was added.
