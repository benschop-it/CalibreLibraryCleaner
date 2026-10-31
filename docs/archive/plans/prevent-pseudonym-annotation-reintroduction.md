# Prevent pseudonym annotations returning through metadata review

## Objective

Keep cleaned author names intact when provider metadata contains pseudonym
annotations, including repeated bracketed markers and the `psued.` misspelling.

## Scope

- Reject annotated provider author lists at the shared author-write boundary.
- Keep review proposals, final preview, convergence and both author apply routes consistent.
- Preserve raw provider evidence and all independently applicable non-author fields.
- Invalidate review overrides for the changed application policy.

## Out of scope

Matching changes, author-specific aliases, automatic removal from current catalog
names, provider-cache deletion, library mutation during development and packaging.

## Relevant requirements

- [Functional requirements](../../functional-requirements.md): explicit author review,
  preservation of cleaned pen names and deterministic metadata convergence.
- [Safety](../../safety-and-rollback.md): no inferred real-name substitution; fixed worker only.
- [ADRs 0015](../../adr/0015-persistent-calibre-api-mutation-worker.md) and
  [0021](../../adr/0021-matching-first-performance-oriented-product.md).

## Existing implementation inspected

`EditionMetadataAuthorWritePolicy` handles combined comma-form authors but accepts
provider pseudonym annotations verbatim. Applicability, preview, metadata execution
and final bibliographic-author handoff already use it. The WPF Proposed authors
column instead shows raw candidate authors. Provider caches deliberately retain
original reduced evidence, including annotated names.

## Proposed design

Withhold the complete author field if any provider credit contains a recognized
pseudonym annotation or bounded explicit `psued.` marker. Do not guess which
identity to write or silently remove credits. Existing current author lists remain
unchanged even when the user checks other metadata. Keep raw names visible as
provider evidence with a specific withholding explanation, not as proposed writes.
Version the fused review policy; retain reusable raw provider caches.

## Files expected to change

Domain author-write policy and review version, WPF proposal row/detail bindings,
focused Domain/Application/WPF tests and directly related current documentation.

## Safety considerations

No production library access or mutation. Existing backup, worker, author sorting
and stop-on-ambiguity boundaries stay unchanged. Clean coauthors must not be
partially applied if another credit is withheld. Matching and opt-in Final authors
pseudonym removal remain unchanged.

## Implementation steps

1. Reproduce annotated author writes with generated regressions.
2. Add shared rejection, review disclosure and review-policy invalidation.
3. Verify cached convergence, checked execution and final-author handoff.
4. Run focused checks and standard verification; document actual limitations.

## Tests

- Both reported annotation shapes and arbitrary equivalent names.
- Repeated/malformed brackets, `psued.`, coauthor preservation and legitimate negatives.
- Current names remain identical in checked preview; unrelated fields stay applicable.
- Cached proposals cannot restore author annotations or recreate executable author work.
- WPF separates raw provider evidence from proposed/final author values.

## Verification commands

VSTest/xUnit 2 on SDK 10.0.401; use `dotnet test <project> --no-restore --filter ...`.
Then `dotnet build --no-restore`, `dotnet test --no-build --maxcpucount:1`,
`dotnet format --verify-no-changes --no-restore`, `git diff --check`.
Do not terminate the user's running application or use alternate output directories
to bypass build locks. Record any actual lock and request the application be closed.

## Risks

Marker detection must not reject ordinary words such as pseudoscience or names
containing similar letters. Reject only bounded explicit marker tokens; keep
uncertain raw evidence visible. Existing overrides must not retain stale author-write semantics.

## Unresolved questions

None. Preserve current authors rather than infer replacements from provider annotations.

## Progress

- [x] Inspected shared write, applicability, preview, execution and review surfaces.
- [x] Clean worktree at start; running development application detected, PID 2472.
- [x] Reproduced nine failing assertions for the reported and equivalent generated
  annotation patterns before the policy change.
- [x] Shared rejection and WPF prepared-proposal/raw-evidence separation implemented.
  Fused review policy advanced from `1.2.0` to `1.3.0`; provider caches and matching
  policies are unchanged.
- [x] Focused Domain checks passed: 35 tests covering preparation, checked preview,
  field applicability, coauthor retention, negatives and versioned fusion.
- [x] Focused Application checks passed: 6 tests covering both reported shapes in
  checked execution, final-author repair handoff, cached convergence and stale
  override rejection without provider requests.
- [x] Added WPF regression tests for annotated evidence disclosure and combined
  author preparation. Repository formatting verification passed.
- [x] Complete available suites passed: Domain 443/443, Application 301/301, plus
  2/2 persisted metadata-review-decision checks (746 total, no skips). Changed
  production diff, scope, plan links and `git diff --check` were reviewed/passed.
- [x] Earlier normal solution build and WPF validation were blocked, not waived:
  `dotnet build --no-restore --verbosity minimal` failed MSB3027/MSB3021 because
  cleaner PID 2472 and Visual Studio PID 45264 lock the normal WPF output DLLs.
  Requested closure through the clarification tool; user unavailable. No process
  was terminated and no alternate configuration/output directory was used.
- [x] User closed the cleaner on 2026-10-24; normal output locks were released.
  The initial build then reported missing generated WPF `.g.cs` files. A standard
  `dotnet build --no-restore --no-incremental --verbosity minimal` regenerated
  build artifacts and succeeded with zero warnings/errors, without alternate
  configuration/output paths or terminating any user process.
- [x] All 8 metadata-review row tests passed, including withholding annotated
  provider authors, original evidence disclosure and prepared-author preview.
  The full `dotnet test --no-build --maxcpucount:1 --verbosity minimal` passed
  1,216 tests: Domain 443, Application 301, Infrastructure 366, Architecture 34,
  WPF 72; no skips. This includes the companion duplicate-EPUB-manifest fix.
- [x] Formatting verification, `git diff --check`, and
  `dotnet list package --vulnerable --include-transitive --no-restore` passed;
  no vulnerable packages were reported. Documentation reconciled and plan archived.

## Final outcome

Implemented and verified in the normal Visual Studio Debug outputs. Provider
pseudonym annotations cannot become writable authors or return through cached
metadata/final-repair handoff; WPF distinguishes withheld fields from raw evidence.
The user can start the updated application with F5. No personal library or live
review selection was modified, no package was required, and no commit was created.
