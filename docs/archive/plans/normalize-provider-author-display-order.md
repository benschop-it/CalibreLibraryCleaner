# Normalize provider author display order

## Objective

Make metadata review propose and write `Patrick Henry`, not `Henry, Patrick`,
when the explicit family/given notation is `Henry| Patrick` or `Henry, Patrick`.
Also preserve the specified dot-and-space initial typography: `A.C. Baantjer`
must prepare as `A. C. Baantjer`, not undo an already canonical current name.

## Scope

Shared preparation of individual and combined explicit two-part provider names,
preview/write consistency, cached convergence and final-author repair handoff.
Alternating pipe-separated family/given pairs must also produce separate ordered
author values (for example `Lannert| Stacy| Kemp| Kristen`).
Reuse existing display harmonization for every prepared provider credit after
name-order handling; do not duplicate its initials regular expressions.

## Out of scope

Guessing names without explicit separators, author-specific aliases, duplicate
matching changes, removing qualifiers/life dates, mutation-engine changes or packaging.

## Relevant requirements

- [Functional requirements](../../functional-requirements.md): display names versus
  author sorts, reviewed metadata and conservative name normalization.
- [Safety](../../safety-and-rollback.md): complete credits, exact supported read-back.
- [Architecture](../../architecture.md): one preparation policy shared by review and execution.

## Existing implementation inspected

`EditionMetadataAuthorWritePolicy` converts comma-form names only inside combined
` & ` credits. Individual names are returned verbatim. WPF now uses this shared
prepared value, so both its preview and execution inherit the gap. Existing
`PipeAuthorNameNormalizationPolicy` already returns separate display/sort values.
Final bibliographic-author handoff compares raw, rather than prepared, authors
before deciding whether a correction remains. Regression execution also showed
`EditionMetadataCandidate` alphabetically sorting separate provider coauthors,
which reversed the expected credit order.

## Proposed design

Reuse existing family/given normalization for a two-part comma form or bounded
alternating pipe-separated family/given pairs.
Keep undelimited display names unchanged and reject ambiguous/multipart qualified
credits instead of guessing or partially applying coauthors. Pseudonym withholding
still runs first. Final repair compares the prepared list for convergence.
Preserve provider credit order in candidates. Advance fused review policy and the
proposal interpretation version: older cache entries discarded source ordering
and cannot prove original contributor order, so become misses rather than being
reused through identifier aliases. Do not delete those artifacts or reconstruct
order by author-specific rules. Normal review/request budgets remain unchanged.

## Files expected to change

Domain author-write policy/review version, Application final-author handoff,
focused Domain/Application/WPF tests and directly related documentation.

## Safety considerations

No library read/write during development. Ordinary author-sort values remain
family-first where supported; display authors are given-first. No automatic
arbitrary identity replacement or qualifier removal is introduced.

## Implementation steps

1. Reproduce singleton pipe/comma display and repeat-review regressions.
2. Reuse shared explicit-order normalization across writable author preparation.
3. Fix final handoff equality and verify actual planned author payloads.
4. Run normal solution/WPF verification once the user closes the debug app.

## Tests

Reported example, particles/initials/non-Latin names, lists and combined credits,
idempotence, ambiguous qualifiers, pseudonym regression, preview/worker agreement,
already-normalized current credits and no repeated final-author repair.
Canonical initials, compact initials after comma/pipe conversion, cached
already-applied canonical names, actual worker values and no coauthor collisions.

## Verification commands

Filtered Domain/Application/WPF suites, standard normal solution build/tests,
formatting and whitespace checks. No user-process termination or alternate outputs.

## Risks

Explicit two-part delimiter notation must not turn into general cultural/name-order
guessing. Ambiguous extra components are withheld and raw provider evidence remains
visible. Shared preparation must not reinstate pseudonym annotations.

## Unresolved questions

None. Given-first display and family-first sort remain separate values.

## Progress

- [x] Inspected shared preparation and existing pipe/sort normalization.
- [x] Preserved earlier fixes; cleaner PID 60868 remains running.
- [x] Reproduced 22 failing Domain assertions for individual/multi-author display,
  preview/defaults and ambiguous input before implementation.
- [x] Shared explicit-order preparation implemented with the existing normalizer,
  bounded even pipe pairs, separate ordered authors and whole-field withholding
  for ambiguous qualifiers. Existing pseudonym protection remains first.
- [x] Corrected a coupled provider-candidate ordering bug found by actual payload
  tests: candidates now retain source credit order rather than alphabetizing.
  Fused review policy initially advanced to `1.4.0`; proposal interpretation/cache identity is `1.1.0`.
  Old order-losing cache data is a miss, not reconstructed or deleted.
- [x] Prepared values now govern automatic-casing checks and final-author handoff
  equality, preventing accidental auto-reordering or repeated author writes.
- [x] Focused checks passed: 59 Domain, 14 Application and 24 Infrastructure/cache
  checks. Complete suites passed: Domain 467, Application 310, Infrastructure 368
  (1,145 total, no skips), before adding the final isolated pair-bound test.
- [x] Added WPF tests for both user examples, including two separate coauthors,
  raw provider evidence, checked preview and unchecked current-value preservation.
  Final Domain run including the pair-bound regression passed 468/468, for 1,146
  passing Domain/Application/Infrastructure checks in total. Formatting,
  `git diff --check` and complete production-diff review passed.
- [x] Follow-up initial-spacing regression reproduced nine failing assertions:
  compact provider initials were passing through unchanged and could replace
  canonical current spacing. Shared preparation now reuses the existing display
  harmonizer after order/credit preparation. It does not add typography regexes,
  infer names or change matching; qualified names remain unmodified by typography.
  Coauthor-normalization collisions withhold the complete author field.
- [x] Fused review semantics now `1.5.0`; raw proposal semantics stay `1.1.0`.
  Compact-initial cached source evidence remains reusable without new requests,
  while stale Apply choices cannot carry the older display semantics.
- [x] Initial-spacing focused checks passed: 85 Domain, 20 Application and 24
  Infrastructure/cache tests. Added WPF proposal/preview regressions and actual
  checked-payload, no-author-write and cached no-op cases. Final combined suite
  results are recorded after the source freeze below.
- [x] Updated WPF verification/rebuild initially required closing the user's cleaner
  (PID 60868). Requested closure; user unavailable. Do not terminate the process or
  use alternate build outputs. The earlier Expanded-summary fix remains pending
  the same normal-output rebuild. The debug app subsequently closed and the
  output lock was released without terminating any user process.
- [x] Normal `dotnet build --no-restore --no-incremental --verbosity minimal`
  succeeded with zero warnings/errors. Complete
  `dotnet test --no-build --maxcpucount:1 --verbosity minimal` passed 1,290 tests:
  Domain 478, Application 320, Infrastructure 368, Architecture 34, WPF 90;
  none skipped. This includes the initials-spacing, contributor-order, cache,
  actual author-payload, final-handoff and prior regression checks.
- [x] `dotnet format --verify-no-changes --no-restore --verbosity minimal`,
  `git diff --check`, source-diff review and local plan links passed.
  A subsequent normal incremental build also passed, and the dependency audit
  reported no vulnerable packages without restoring unchanged dependencies.
  Documentation reconciled and plan archived. No alternate build outputs,
  library mutation, packaging or commit was required.

## Final outcome

Implemented, rebuilt and verified in normal Visual Studio Debug outputs.
Metadata preparation uses Patrick Henry, separate ordered Stacy Lannert / Kristen
Kemp credits and canonical A. C. Baantjer initials. Raw provider evidence remains
separate; pseudonym withholding, review requirements and matching are preserved.
The updated application is ready for F5. No personal library or live review state
was modified, no package was needed and no commit was created.
