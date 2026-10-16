# Normalize pseudonym annotations

## Objective
Ignore explicit pseudonym annotations during matching and offer reviewed removal
from retained author metadata before Calibre's existing managed-name refresh.

## Scope
One provider-neutral bounded annotation policy; exact-metadata and Expanded author
identity normalization; Final authors proposals; policy version invalidation;
focused tests, matching calibration/holdout gates, and current documentation.

## Out of scope
Real-name substitution, library-specific aliases, direct folder/file renames,
early metadata mutation, new workflow stages, and guessing truncated folder names.

## Relevant requirements
Same work/language precision and recall; preserve original evidence; reviewed
supported Calibre writes only; version all matching/review/cache identities.

## Existing implementation inspected
MetadataTextNormalizer, CandidateMetadataNormalizer, AuthorSourcePreservationPolicy,
PipeAuthorNameNormalizationPolicy, Candidate preparation and Final authors contracts,
provider validation identities, existing final-author worker and executor harness.

## Proposed design
Strip only explicit balanced pseudonym annotations or recognized terminal suffixes
from full catalog author names. Preserve the published pen name and unrelated
brackets. Share the policy across matching and Final authors. Matching remains
read-only; permanent cleanup is an unchecked final-author proposal applied through
the existing worker. Flag malformed/ambiguous text rather than inventing names.
Use an ordinary-name fast path and bounded generated regular expressions.

## Files expected to change
Domain annotation policy, author normalizers and versions; final-author policy and
existing tests; Application preparation and executor tests; WPF row regression;
workflow versions, matching baseline policy metadata, and current requirements.

## Safety considerations
No real library mutation or direct filesystem rename. Do not equate pen names with
the real person named in an annotation. Preserve language/identifier/content
contradictions, original author evidence, keeper overrides, and Skip. No book text,
metadata, provider payloads, or paths are added to ordinary logs.
The existing fixed worker, confirmation, exact current review identity, and
read-back checks remain the only authority for permanent author/name changes.

## Implementation steps
1. Add a bounded annotation parser and focused positive/negative tests.
2. Integrate matching identity and reviewed Final authors proposals with versions.
3. Run matching corpus and review regressions; reconcile docs and standard gates.

## Tests
Bracketed/prefix/infix/suffix markers; unaffected roles and ordinary names;
malformed input, bounds, idempotence, multi-author identity and unchanged sources;
false-positive cases for distinct pen names/real names; final-review opt-in;
calibration/holdout metrics and cache/workflow version identities.
Executor tests verify checked/unchecked author writes through the existing worker.
WPF tests verify unchecked Needs review presentation without adding a new UI.

## Verification commands
All standard gates passed on 2026-10-16:

- `dotnet restore`
- `dotnet build --no-restore`
- `dotnet test --no-build --maxcpucount:1`: 1,046 passed, zero failed or skipped.
- `dotnet format --verify-no-changes`
- `git diff --check`
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

The frozen matching corpus retained 100% precision, 95.6897% recall, 100% candidate
route recall, and zero overmerged/cross-language groups. Supplementary labeled
pseudonym scenarios achieved 100% precision/recall in both calibration and holdout.
Only policy-version fields changed in the frozen baseline; metrics and labels did not.

## Risks
Overbroad annotation stripping can conflate authors. Explicit markers and reviewed
permanent writes limit this risk. Old persisted candidate/final-review identities
are incompatible with the changed policy. Restart the updated app and run explicit
Rescan plus Candidate analysis; old displayed groups are not updated in place.
No real-library destructive acceptance or folder renaming was performed in this task.

## Unresolved questions
None for the approved two-stage design.

## Progress
User approved implementation after the workflow-placement review. Relevant current
worktree changes staged as a rollback point before editing. The parser slice passed
before integration. Explicit markers are ignored in read-only identities and become
unchecked Final authors proposals; malformed markers remain unchanged and flagged.
Raw names, unrelated qualifiers, coauthors, and worker boundaries are preserved.
No new mutation stage or UI was added.
Matching versions are exact-metadata/1.2.0 and work-language-matching/1.5.0;
workflow identities are exact-analysis/1.2.0 and candidate-analysis/1.4.0.
Suspicion/source versions are author-suspicion/1.1.0 and
author-source-preservation/1.4.0. Provider validation identities are
edition-metadata-query/1.5.0 and bibliographic-resolution/1.1.0, invalidating cached
evidence whose acceptance depends on the changed author identity comparison.
The first corpus run exposed an explicit eligibility-version list; adding V6 kept
advisory classifications unchanged. The first full suite exposed one provider test
pinned to V5; updating it to V6 restored the suite. Scoped mechanical formatting
fixed only the new proposal block and parser final newline. Final full suite passed.
Production/test diffs, policy invalidation, documentation, and logging reviewed.

## Final outcome
Completed. Recognized pseudonym annotations no longer prevent read-only author
identity matches. After consolidation, retained annotated authors appear as
unchecked Final authors proposals. Explicit Apply and the existing confirmed
Calibre worker persist approved names; existing managed-name refresh handles folders.