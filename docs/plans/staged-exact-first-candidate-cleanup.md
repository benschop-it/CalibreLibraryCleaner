# Staged Exact-First and Unified Candidate Cleanup

## Objective

Replace the current one-scan, three-category cleanup architecture with a staged workflow optimized for large libraries:

1. perform an exact-only analysis;
2. run the existing Exact cleanup algorithm unchanged;
3. perform a trusted post-exact catalog refresh that reuses known fingerprints and analysis artifacts instead of hashing every surviving file again;
4. discover one unified set of residual Metadata/Expanded candidate groups; and
5. run one Candidate cleanup workflow.

During development, expose two primary workflow buttons: `Exact cleanup` and `Candidate cleanup`. `Candidate cleanup` becomes enabled only after Exact cleanup completes successfully, including a successful nothing-to-do result. A future plan may combine both stages behind one user action.

## Scope

- Preserve the current Exact binary grouping, retention ranking, transfer/removal planning, fixed-worker execution, typed deltas, and failure behavior.
- Add an exact-only initial analysis mode that reads the catalog, resolves paths, hashes formats, and publishes Exact groups without EPUB/PDF assessment, recommendations, or Expanded discovery.
- Persist a workflow phase tied to the authoritative state generation and revision so button availability survives restart.
- Add a read-only post-exact catalog refresh that reconciles Calibre's current catalog with the application-owned projected state and completed mutation deltas.
- Reuse known fingerprints for unchanged associations; hash only affected or otherwise unexplained associations permitted by the refresh contract.
- Fail closed and require a full exact analysis when catalog reconciliation finds changes not explained by the completed Exact cleanup.
- Rebind reusable EPUB/PDF assessments by fingerprint and reuse cached candidate EPUB signatures by fingerprint/version.
- Keep exact normalized title/author detection as mandatory candidate evidence, but remove Metadata candidates as a separate cleanup category and UI workflow.
- Combine exact-metadata and Expanded evidence into one versioned, disjoint unified candidate-group model.
- Show the generated keeper's title and authors in the unified candidate group grid and update them after keeper override.
- Preserve advisory `Cleanup eligible` and `To be reviewed` text; both types remain selected by default and can be skipped.
- Add one Candidate cleanup planner/executor using the fixed persistent worker and current external-backup, lease, marker, chunk, delta, uncertainty, and checkpoint rules.
- Retire `Cleanup all` and the standalone Metadata/Expanded mutation commands only after the staged replacement reaches behavioral and safety parity.
- Add structured timing/count logs for every stage without logging paths, titles, authors, or book content.

## Out of scope

- Changing the Exact cleanup algorithm or its retention order.
- Combining Exact cleanup and Candidate cleanup into one click in this delivery.
- Skipping SHA-256 during the initial exact-only analysis.
- Trusting timestamps or size alone as file identity.
- Detecting arbitrary external Calibre changes while the cleaner is running beyond the post-exact reconciliation boundary.
- Direct writes to `metadata.db` or direct mutation of Calibre-managed files.
- A second mutation engine, direct-command fallback, or changes to the fixed persistent `calibre-debug` worker rule.
- Automatic title, author, identifier, series, language, cover, or other metadata rewriting.
- New PDF cross-document matching, OCR, online lookup, embeddings, or AI adjudication.
- Reworking candidate confidence thresholds except where required to carry exact-metadata-only candidates into the unified result.
- Removing the existing persistence format before migration and restart compatibility are proven.

## Relevant requirements

- Exact cleanup is independently executable and remains the first destructive stage.
- Every initial exact analysis hashes every current format using streaming SHA-256 and bounded concurrency.
- Exact cleanup requires explicit confirmation of a complete external backup.
- Candidate cleanup is disabled until Exact cleanup completes successfully for the current workflow generation.
- Candidate preparation must not silently accept unexplained catalog or file changes.
- Only supported Calibre APIs through the fixed persistent worker may mutate the library.
- Complete successful worker chunks publish durable typed deltas; ambiguous or failed mutation marks state uncertain and blocks Candidate cleanup until a new exact analysis succeeds.
- Cleanup never triggers a full scan implicitly.
- The runtime single-writer assumption remains: no unrelated process changes the selected library while the cleaner is active.
- Persisted fingerprints and assessments are reusable only under explicit version and reconciliation rules.
- Exact-metadata candidates must not disappear merely because EPUB content evidence is absent.
- Candidate evidence remains local, deterministic, explainable, and content-safe.
- Every candidate group has one generated keeper, optional keeper override, and session-scoped Skip choice.
- Candidate cleanup transfers complementary formats before removals and removes a record only after it is proven empty.

## Existing implementation inspected

- `ScanLibraryUseCase` always reads the complete catalog, resolves every format, hashes every format, assesses EPUB/PDF files, detects Exact and Metadata groups, runs Expanded discovery, and generates recommendations.
- `AssessmentReusePolicy` reuses EPUB/PDF assessment facts by fingerprint and version, but only after the current Scan hashes every file.
- Candidate EPUB signatures are already cached outside the library by fingerprint and analyzer/policy/resource versions.
- `ExecuteBulkExactDuplicateCleanupUseCase` implements the accepted Exact cleanup behavior and must remain unchanged.
- Successful cleanup chunks update authoritative state through `LibraryStateDelta`; transfers currently become `ProjectedPresent` formats carrying a known fingerprint but no physical path.
- State projection recomputes Exact groups, filters Metadata groups, drops consolidation recommendations, and drops Expanded evidence after mutation.
- `ExactMetadataDuplicateDetector` is cheap, deterministic, and independent of EPUB/content evidence.
- Expanded generation uses compatible author identities, work-level evidence, mutual top-20 ordinary candidates, candidate limits, language/series contradictions, candidate-only EPUB signatures, and constrained clustering.
- Metadata and Expanded groups are not subsets of one another. Metadata can exist without usable EPUB/content evidence; Expanded can connect title/author variants that exact metadata normalization does not.
- The current WPF exposes separate Exact, Metadata, and Expanded tabs plus category cleanup commands and `Cleanup all`.
- Expanded member rows already contain title and authors. The upper group grid exposes keeper ID but not keeper title/authors.
- The current persisted state does not record a durable staged-workflow phase.
- Current Serilog diagnostics provide durable phase and cleanup logs under `%LOCALAPPDATA%\CalibreLibraryCleaner\logs`.

## Proposed design

### Target workflow

```text
Select library
    |
    v
Exact-only analysis
(catalog + safe paths + full SHA-256 + Exact groups)
    |
    v
Exact cleanup
(existing algorithm and worker path unchanged)
    |
    v
Trusted post-exact refresh
(catalog reread + projected-state reconciliation + fingerprint reuse)
    |
    v
Residual candidate analysis
(EPUB assessment + exact metadata evidence + Expanded evidence + cached signatures)
    |
    v
Unified candidate review
(keeper title/authors + evidence + advisory eligibility + Skip)
    |
    v
Candidate cleanup
(one worker-only transfer/removal graph)
```

### Durable workflow phase

Add a versioned workflow checkpoint outside the Calibre library and bind it to one canonical library root, state generation, state revision, and all relevant policy versions.

Suggested phases:

- `RequiresExactAnalysis`: no compatible authoritative exact-only baseline exists.
- `ExactReady`: exact-only analysis completed and Exact cleanup may run.
- `ExactCleanupRunning`: represented durably by the existing mutation intent; not a separate source of mutation truth.
- `CandidatePreparationReady`: Exact cleanup completed or returned nothing to do; Candidate cleanup button is enabled.
- `CandidateAnalysisReady`: post-exact refresh and unified candidate discovery completed for the current generation/revision.
- `CandidateCleanupRunning`: represented by the existing mutation intent.
- `Completed`: Candidate cleanup completed or returned nothing to do.
- `Uncertain`: derived from `LibraryStateStatus.Uncertain`; both mutation commands are disabled until a new exact analysis.

The checkpoint is published atomically with or immediately after the authoritative state transition it describes. It must never claim a later phase than the durable state and mutation marker support.

A successful exact-only analysis starts a new generation at `ExactReady`. A successful Exact cleanup, including nothing-to-do, advances to `CandidatePreparationReady`. Trusted refresh and reusable-fact preparation start a new physical authoritative generation that remains at `CandidatePreparationReady`, with provenance linking it to the exact generation/revision. Residual unified candidate discovery later advances that same generation to `CandidateAnalysisReady`. Candidate cleanup projects deltas in that generation and advances to `Completed` only after the final successful checkpoint.

### Development UI state machine

Keep the existing library selection and explicit Scan/Load controls during migration, but replace the destructive top-level workflow with two primary buttons:

- `Exact cleanup`
    - enabled only in `ExactReady` with current authoritative state;
  - invokes the existing Exact cleanup confirmation and execution path unchanged;
    - when no eligible unskipped selection requires mutation, invokes the existing
        nothing-to-do path, records successful completion, and enables Candidate cleanup;
  - remains disabled after completion for that workflow generation.

- `Candidate cleanup`
  - disabled before `CandidatePreparationReady`;
  - first activation in `CandidatePreparationReady` performs the trusted refresh and candidate analysis, populates the unified candidate workspace, and stops without mutation;
  - remains the same visible primary button, with status text stating that candidate review is ready;
  - activation in `CandidateAnalysisReady` builds the candidate plan, requests external-backup confirmation, and executes the current keeper/Skip selections;
  - never prepares candidates and mutates them in the same activation because the user must have an opportunity to inspect title, author, keeper, evidence, and Skip state.

Button command state, accessible name, tooltip, status, and progress text must distinguish preparation from execution even though the visible primary caption remains `Candidate cleanup`.

On restart, persisted workflow phase and authoritative state restore the correct button states. Session-scoped keeper overrides and Skip choices reset when candidate analysis is reloaded unless a separate reviewed-selection persistence decision is made later.

### Exact-only analysis

Introduce an explicit analysis mode rather than optional-null-service behavior:

- `ExactOnly`
- `CandidateResidual`
- retain `FullCompatibility` temporarily for migration/tests, then remove when no callers remain.

`ExactOnly` performs:

1. library validation;
2. read-only catalog load;
3. canonical path resolution and containment checks;
4. streaming SHA-256 of every resolvable format;
5. immutable book/finding construction;
6. Exact binary grouping; and
7. authoritative baseline publication at `ExactReady`.

It does not invoke EPUB assessment, PDF assessment, exact-metadata grouping, recommendation generation, candidate content extraction, or Expanded clustering. Exact keeper selection continues to use catalog metadata, identifiers, cover evidence, and record format count exactly as it does today.

The UI and log explicitly identify this as exact-only analysis so users do not mistake it for completed candidate discovery.

### Trusted post-exact refresh

Candidate preparation begins with a dedicated `RefreshAfterExactCleanupUseCase`; it is not a normal Scan.

The refresh:

1. loads the authoritative projected state, exact mutation intent/history, and workflow checkpoint;
2. validates the library root and reads `metadata.db` read-only;
3. resolves all current catalog format paths through the existing path boundary;
4. constructs the expected post-exact record/format inventory from the completed typed deltas;
5. compares catalog record IDs, metadata, canonical format labels, and associations with that expected inventory;
6. maps each current association to a known pre-cleanup or transferred fingerprint;
7. hashes only affected transfer targets or explicitly permitted unexplained associations that require byte confirmation;
8. rejects any catalog difference that cannot be explained by completed Exact cleanup and directs the user to run a new exact analysis; and
9. publishes a new physical `Present` snapshot generation with current paths/observations and fingerprint provenance.

Fingerprint reuse must be based on the authoritative pre-exact fingerprint plus a completed typed mutation relation, never on timestamp/size alone. A transfer target may inherit the source fingerprint only when the worker result and typed delta identify that exact transfer; the implementation should still target-hash transferred files if the current worker protocol cannot prove resultant bytes. Unexpected new records, removed records, changed metadata, added formats, missing formats, or changed associations fail closed in the first slice rather than being silently incorporated.

The refresh records counts and durations for:

- catalog records/formats read;
- unchanged fingerprints reused;
- transferred fingerprints rebound;
- targeted files hashed;
- unexplained differences;
- assessment facts rebound; and
- total refresh time.

No paths, titles, authors, identifiers, or content are logged.

### Residual candidate analysis

Run expensive analysis only after exact copies and empty records have been removed.

Initial safe implementation order:

1. Rebind any compatible persisted EPUB assessments by fingerprint/version.
2. Assess remaining EPUB targets that lack reusable facts.
3. Run exact normalized metadata detection over the refreshed books.
4. Build Expanded profiles and bounded candidate pairs.
5. Resolve candidate EPUB signatures from the existing no-prose cache, inspecting only cache misses.
6. Cluster Expanded evidence.
7. Build unified candidate groups.
8. Assess/rebind PDFs only for records that enter unified candidate groups, because PDF assessment does not participate in current discovery but can inform keeper ranking.
9. Recompute the unified keeper after candidate-target PDF assessments complete.

If moving PDF assessment after discovery would alter an accepted current invariant, keep surviving-PDF assessment in its existing position for the first migration slice and optimize it only after measured parity.

### Unified candidate evidence

Keep exact Metadata detection, but change its architectural role from a separate cleanup authority to a mandatory evidence source in unified candidate discovery.

Requirements for the unified model:

- Every exact normalized title/author group enters candidate construction even when EPUB content is absent or unavailable.
- Exact-metadata candidates are not subject to the ordinary mutual top-20 cap.
- Expanded candidate generation retains current bounded author-first behavior and provenance.
- The final model stores provenance for exact metadata, identifier, title, author, series, language, binary, and content evidence.
- Final groups are disjoint and reference current records only.
- Do not use blind connected-component closure over weak or contradictory bridges.

Suggested deterministic merge policy:

1. Start with current disjoint Expanded groups as strong candidate components.
2. Process exact-metadata groups in canonical identity/group-ID order.
3. If a Metadata group intersects zero Expanded components, create a Metadata-evidence component.
4. If it intersects one Expanded component, attach compatible unassigned Metadata members and add Metadata evidence.
5. If it intersects multiple Expanded components, merge only when complete-component language, series/index, identifier, edition-marker, and content contradictions permit the union.
6. When a complete merge is not permitted, preserve the Expanded components, attach compatible unassigned members deterministically, and add a visible cross-component Metadata-overlap review finding instead of duplicating a record across groups.
7. Run invariant validation proving every record belongs to at most one executable unified group.

The persisted group classification remains advisory:

- `Cleanup eligible`: strongest current evidence policy.
- `To be reviewed`: exact-metadata-only, incomplete/ambiguous content, older evidence, or another policy-defined uncertainty.

Both begin unskipped and are included in Candidate cleanup unless the user checks Skip.

### Unified keeper and presentation

Create a unified retention policy from the existing deterministic candidate ranking:

1. completed assessment count;
2. total assessment score;
3. present format count;
4. metadata completeness;
5. valid strong identifiers;
6. cover presence; and
7. lowest record ID.

The group row exposes computed values from the current keeper:

- `KeeperTitle`;
- `KeeperAuthors`;
- `KeeperRecordId`;
- language;
- evidence classification;
- record count;
- source evidence badges; and
- concise content comparison summary.

Changing the keeper raises property notifications for title, authors, ID, and member actions. The group grid puts keeper title and authors before technical evidence so the list can be scanned efficiently. The member grid retains full title, authors, formats, language, series, keeper-ranking facts, and viewer launch.

### Candidate cleanup

Replace standalone Metadata cleanup, standalone Expanded cleanup, and `Cleanup all` with one residual Candidate cleanup planner/executor after parity is proven.

The planner:

- accepts one keeper/Skip selection per current unified group;
- validates generation, revision, group ID, members, keeper, physical facts, and fingerprints;
- transfers one selected complementary source for formats absent from the keeper;
- retains the keeper's existing same-format file;
- removes every format from non-keepers;
- removes each non-keeper only after simulated final inventory proves it empty;
- skips stale or physically incomplete groups before mutation and reports counts;
- orders all transfers before format removals and all format removals before record removals; and
- emits deterministic category-neutral operation IDs.

The executor uses one backup confirmation, mutation lease, persistent worker, mutation marker, bounded chunk stream, projected delta stream, uncertainty transition, and final checkpoint. It must not rescan or inspect content during mutation.

### Migration and compatibility

Implement the target in vertical slices while keeping the current workflow runnable until replacement parity is demonstrated:

- Existing persisted full snapshots may be loaded for inspection.
- A compatible full snapshot can seed the exact stage, but Candidate cleanup still requires successful Exact cleanup/nothing-to-do and a new post-exact refresh.
- Existing assessment and content-signature caches remain reusable by fingerprint/version.
- Additive persistence readers must tolerate workflow fields being absent and map them conservatively to `RequiresExactAnalysis` unless a migration rule proves `ExactReady`.
- Do not delete `ExecuteCompositeCleanupUseCase`, Metadata cleanup, Expanded cleanup, or their UI commands until the new Candidate planner/executor passes equivalent worker, uncertainty, and projection tests.
- During the transition, hide legacy destructive commands behind one development switch or mark them unavailable when staged mode owns the selected library; never allow both architectures to mutate the same generation.
- After parity, remove `Cleanup all`, the separate Metadata/Expanded cleanup commands, obsolete composite reconciliation code, and obsolete documentation in a dedicated cleanup slice.

## Files expected to change

### Documentation and decisions

- New `docs/adr/0020-staged-exact-first-candidate-cleanup.md` superseding the one-scan cleanup portions of ADR 0018/0019 and the composite-cleanup design.
- `docs/product-vision.md`
- `docs/functional-requirements.md`
- `docs/architecture.md`
- `docs/domain-model.md`
- `docs/safety-and-rollback.md`
- `docs/test-strategy.md`
- `docs/roadmap.md`
- `README.md`
- This plan and the superseded status/final notes in `docs/plans/composite-reviewed-cleanup.md`.

### Domain

- New versioned workflow phase/checkpoint values under `CalibreLibraryCleaner.Domain/Libraries`.
- New unified candidate group, evidence provenance, review classification, and retention values under `Domain/Matching` or a dedicated `Domain/Candidates` namespace.
- Existing exact Metadata detector retained.
- Existing Expanded policies adapted to produce evidence for the unified model.
- Existing `LibrarySnapshot`/state projection extended only as required for staged provenance and unified groups.

### Application

- Explicit analysis-mode contract or separate `AnalyzeExactLibraryUseCase`.
- New `RefreshAfterExactCleanupUseCase` and catalog/projected-state reconciliation policy.
- New fingerprint reuse/rebind values that do not leak filesystem or SQLite types into Domain.
- New unified candidate discovery/orchestration use case.
- New unified candidate cleanup contracts, planner, executor, progress, and structured logs.
- `LibraryStateSession` and persistence ports extended for workflow checkpoint publication.
- Existing assessment reuse and content cache orchestration reused rather than duplicated.
- Existing Exact cleanup use case composed unchanged.

### Infrastructure

- Versioned state manifest/baseline/checkpoint serialization for workflow phase and refresh provenance.
- Read-only catalog adapter reused by post-exact refresh.
- Existing path resolver and hasher reused for reconciliation and targeted hashing.
- Existing EPUB/PDF inspectors and signature cache reused.
- Existing fixed persistent mutation worker reused without a fallback.

### WPF

- Main workflow state and two primary buttons in `MainWindow.xaml`/`MainWindowViewModel`.
- Exact workspace wired to durable phase transitions without changing its cleanup policy.
- New unified candidate workspace/group/member ViewModels.
- Keeper title/author columns and update notifications.
- Candidate preparation/review/execution status and progress.
- Legacy Metadata, Expanded, and Cleanup-all destructive controls removed only in the final migration slice.

### Tests

- Domain tests for workflow transitions, unified grouping, evidence provenance, disjointness, advisory classification, and keeper ranking.
- Application tests for exact-only analysis, refresh reconciliation, fingerprint reuse, targeted hashing, unified discovery, candidate planning/execution, persistence, failures, and cancellation.
- Infrastructure tests for versioned persistence and synthetic catalog refresh scenarios.
- WPF tests for button gating, restart restoration, candidate preparation versus execution, title/author display, keeper updates, Skip, and close protection.
- Architecture tests preserving dependency direction and worker-only mutation.

## Safety considerations

- The Exact cleanup implementation is a fixed dependency of this plan; do not refactor it while introducing staged orchestration.
- Exact-only analysis always hashes every current format.
- Fingerprints may be reused after Exact cleanup only when authoritative typed deltas and read-only catalog reconciliation explain the association.
- Never infer identity from timestamp, size, path, stored name, or record ID alone.
- If catalog reconciliation observes any unexplained addition, removal, metadata change, format association, or path anomaly, stop Candidate preparation and require a new exact analysis.
- Candidate preparation creates a new authoritative generation only after complete successful reconciliation and analysis.
- A canceled/failed candidate preparation leaves the prior post-exact state authoritative at `CandidatePreparationReady`; it does not publish a partial candidate generation.
- A failed or ambiguous Exact/Candidate mutation marks state uncertain and disables both cleanup buttons until a new exact analysis.
- No Candidate cleanup plan may contain `ProjectedPresent` path placeholders; post-exact refresh must bind every executable format to a current physical path and verified or safely inherited fingerprint.
- Candidate cleanup simulation must prove transferred formats survive, keeper formats are not removed, and every record removal is empty.
- The two mutation stages require separate external-backup confirmations because they are separate runs.
- No logs contain book titles, authors, paths, identifiers, or content.
- Automated tests use only synthetic/disposable Calibre libraries.

## Implementation steps

1. **Accept the architecture decision.** Write ADR 0020, amend authoritative requirements/architecture/safety documents, mark the one-scan composite workflow as superseded for future development, and freeze the Exact cleanup algorithm as an unchanged dependency.
2. **Add workflow phase values and persistence.** Implement conservative migration, atomic checkpoint publication, generation/revision/policy binding, restart replay, and state invariants without changing current UI behavior.
3. **Introduce explicit exact-only analysis.** Split orchestration so the exact stage invokes catalog/path/hash/Exact grouping only. Add structured phase metrics and prove EPUB/PDF/matching/recommendation services are never called.
4. **Wire the `Exact cleanup` primary button.** Reuse the existing selection, confirmation, planner, executor, worker, delta, and checkpoint path unchanged. Advance durable workflow phase on Completed/NothingToDo; preserve uncertain-state behavior on failure.
5. **Implement post-exact reconciliation as a pure policy.** Given pre-exact state, typed deltas, and a synthetic fresh catalog, classify every association as unchanged, transferred, expected removed, targeted-hash-required, or unexplained. Add exhaustive tests before filesystem orchestration.
6. **Implement `RefreshAfterExactCleanupUseCase`.** Reuse the catalog reader, path resolver, and hasher; bind physical paths, reuse fingerprints, target-hash only allowed cases, reject unexplained changes, and atomically publish the refreshed generation.
7. **Reuse assessments and content cache after refresh.** Rebind compatible facts by fingerprint/version, inspect misses, log reused/fresh counts, and ensure no stale record/path association survives.
8. **Add unified candidate domain values and merge policy.** Carry exact Metadata evidence without caps, ingest Expanded evidence, enforce disjoint groups and complete-component contradiction rules, classify advisory eligibility, and persist versioned provenance.
9. **Implement residual candidate analysis.** Run EPUB assessment, exact Metadata detection, Expanded discovery/signature reuse, unified grouping, candidate-target PDF assessment, and final keeper ranking in bounded cancellable phases.
10. **Add unified candidate presentation.** Create the group/member rows, show keeper title/authors, update them on override, expose evidence/eligibility/Skip, retain viewer launch, and add efficient filtering/sorting for thousands of groups.
11. **Wire the `Candidate cleanup` primary button.** Gate it on durable phase. First activation prepares and displays candidates without mutation; later activation executes reviewed selections after backup confirmation. Persist phase transitions and protect window closing during long operations.
12. **Implement unified Candidate cleanup.** Reuse current Metadata/Expanded transfer/removal semantics where compatible, one fixed worker, deterministic operations, projected deltas, uncertainty handling, and final checkpoint.
13. **Run shadow parity tests.** On synthetic snapshots, compare legacy Metadata/Expanded candidate membership, keeper ranking, planned transfers/removals, and skip reasons against unified output. Record intentional deviations.
14. **Retire legacy mutation surfaces.** Remove/hide `Cleanup all`, standalone Metadata cleanup, standalone Expanded cleanup, composite reconciliation, and obsolete DI/UI only after all parity and safety tests pass.
15. **Measure the staged workflow.** On the developer's large library, record counts/durations/bytes for exact hashing, exact removals, fingerprint reuse, targeted hashes, EPUB/PDF reuse and inspection, signature cache hits, unified groups, and candidate operations. Do not set acceptance by speculative wall-clock estimates.
16. **Complete repository verification and manual acceptance.** Run standard commands, inspect the complete diff, exercise restart at each durable phase, and verify no worker/test/application processes remain unexpectedly.

## Tests

### Exact-stage preservation

- Exact retention decisions remain byte-for-byte/equivalent for existing fixtures.
- Exact planner operation order and worker protocol remain unchanged.
- Exact-only analysis hashes every current format.
- Exact-only analysis never calls EPUB/PDF inspectors, candidate signature cache, Expanded discovery, or recommendation generation.
- Mixed labels, missing files, multi-target records, complementary transfers, and empty-record removal retain current behavior.
- Exact completion and nothing-to-do both advance to `CandidatePreparationReady`.
- Exact failure/ambiguity marks state uncertain and leaves Candidate cleanup disabled.

### Workflow persistence and UI

- New libraries begin at `RequiresExactAnalysis`.
- Exact analysis advances to `ExactReady`.
- Exact completion enables Candidate cleanup and disables repeat Exact cleanup for the generation.
- Restart restores button availability from durable state.
- Candidate preparation and Candidate execution are distinct activations; first preparation never mutates.
- Candidate analysis completion restores the unified review grid after normal navigation.
- Uncertain state disables both mutation buttons and requires exact analysis.
- Loading legacy state follows the conservative migration rule.

### Post-exact refresh

- Unchanged record/format associations reuse fingerprints without hashing.
- Successful exact removals disappear exactly as projected.
- Successful transfers bind the current target path and expected source fingerprint.
- Transfer targets are target-hashed when protocol evidence is insufficient.
- No timestamp/size-only reuse path exists.
- Unexpected records, formats, removals, metadata changes, paths, or associations fail closed.
- Missing/inaccessible affected files fail refresh without publishing partial state.
- Cancellation leaves the prior authoritative generation and workflow phase intact.
- Refreshed snapshots contain only physical `Present` paths for executable Candidate formats.

### Unified candidate discovery

- Metadata-only groups survive without EPUB/content evidence and display `To be reviewed`.
- Expanded-only title/author variants remain discoverable.
- Compatible Metadata/Expanded overlap produces one disjoint group with combined provenance.
- Contradictory cross-language/series/content bridges do not blindly merge strong components.
- Every record belongs to at most one executable unified group.
- Mandatory exact-metadata candidates bypass ordinary top-20 suppression.
- Candidate/global limits remain deterministic and fail closed.
- Existing signature cache entries are reused by fingerprint/version and contain no prose.
- Keeper ranking uses completed assessments, score, formats, metadata, identifiers, cover, and ID in the documented order.

### Candidate presentation

- Group rows show the current keeper title and authors.
- Keeper title/authors/ID/actions update after override.
- Both eligibility texts start unskipped and remain executable unless skipped.
- Sorting/filtering remains responsive for at least the current 7,000-group scale fixture.
- Double-click launches the selected physical format without changing cleanup state.

### Candidate cleanup

- One unskipped group generates one keeper-authoritative plan.
- Complementary transfers precede format removals; record removals are last.
- Keeper same-format content wins.
- Stale/incomplete groups skip before mutation and are counted.
- One backup confirmation, lease, worker, marker, chunk stream, delta stream, and checkpoint are used.
- Failed/ambiguous worker or delta publication marks state uncertain and stops.
- No scan, catalog read, hashing, assessment, or content inspection occurs during mutation.
- Successful completion advances workflow to `Completed`.

### Architecture and security

- Domain remains independent.
- Application interfaces own catalog refresh, fingerprint reconciliation, persistence, and worker orchestration contracts.
- Infrastructure alone owns SQLite/filesystem/process/parser types.
- WPF contains no cleanup business logic.
- No direct SQLite or Calibre-managed filesystem mutation is introduced.
- Logs contain only technical counts, policy versions, durations, IDs safe under current logging policy, and failure codes.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Manual large-library acceptance records metrics from Serilog and verifies:

- initial exact stage performs full hashing but zero EPUB/PDF/content inspection;
- Exact cleanup uses the existing worker behavior;
- post-exact refresh reuses the expected majority of surviving fingerprints;
- targeted hashes are limited to explained affected associations;
- unexplained changes force a new exact analysis;
- candidate analysis runs only on the residual library and reports assessment/cache reuse;
- keeper title/authors appear in every unified group row;
- restart at `ExactReady`, `CandidatePreparationReady`, and `CandidateAnalysisReady` restores correct button state; and
- no application/test/worker process or file lock remains after normal completion.

## Risks

- Catalog reconciliation after Calibre mutations is the highest-risk new boundary. Calibre may change stored names or paths in ways that must be modeled explicitly without weakening containment or identity checks.
- Reusing fingerprints under the single-writer assumption saves I/O but requires exact provenance from typed deltas; an overly broad reuse rule could associate the wrong bytes with a format.
- The current worker delta records a transferred fingerprint but may not prove resultant target bytes. Targeted post-transfer hashing may be required until the worker protocol provides stronger verification.
- An exact-only first stage defers all format-quality evidence. This is intentional, but it means Exact keeper ranking remains based on its current catalog-level policy rather than EPUB/PDF score.
- Merging Metadata and Expanded evidence can create bridge components. Blind transitive closure could merge different languages or editions; complete-component contradiction checks and disjointness invariants are mandatory.
- Exact-metadata-only candidates intentionally remain executable by default despite weaker evidence. The `To be reviewed` label is advisory, consistent with the accepted high-throughput product decision.
- Two mutation stages require two confirmations and two opportunities for failure. Durable workflow phase and uncertainty handling must prevent stage confusion.
- Persisted workflow migration can accidentally enable Candidate cleanup against an incompatible generation if phase, revision, and policy versions are not bound together.
- Keeping legacy and staged mutation paths during migration risks dual ownership. A selected library generation must be owned by exactly one architecture.
- Removing duplicate files before EPUB/PDF inspection should reduce work, but the actual reduction depends on how many exact copies survive on multi-format keeper records. Acceptance uses measured counts, not predicted timings.
- The running WPF application can lock build outputs; report it and wait for the user to close the application. Tool-created build/test workers may be stopped when they block development.

## Unresolved questions

- Whether the current mutation worker protocol proves transferred target bytes strongly enough to reuse the source fingerprint without a targeted target hash. Resolve with an implementation spike before finalizing refresh rules.
- Whether PDF assessment should move to candidate members only in the first delivery or remain on all residual PDFs until parity is measured.
- Which exact catalog fields Calibre may legitimately change during format transfer/removal and therefore belong in the explained-refresh model. Resolve against disposable Calibre integration fixtures, not the user's library.
- Whether legacy full snapshots can safely migrate directly to `ExactReady` or must always begin at `RequiresExactAnalysis`. Default to the conservative latter state unless a tested proof is added.

## Progress

- [x] Current full scan, Exact cleanup, projected-state, assessment reuse, Metadata detection, Expanded discovery, composite cleanup, persistence, and WPF presentation inspected.
- [x] Product direction agreed: preserve Exact cleanup, stage it first, reuse post-exact facts, unify residual candidates, expose two development-stage buttons, and defer one-click orchestration.
- [x] Executable architecture and migration plan written.
- [x] ADR 0020 accepted and authoritative documentation amended.
- [x] Durable workflow phase implemented.
- [x] Exact-only analysis implemented.
- [x] `Exact cleanup` primary button wired to the unchanged Exact executor.
- [x] Pure post-Exact reconciliation policy implemented.
- [x] Post-exact refresh and fingerprint reuse implemented.
- [x] Assessment and candidate-content cache reuse implemented.
- [x] Unified candidate domain values and merge policy implemented.
- [x] Residual candidate analysis implemented.
- [x] Unified candidate discovery and presentation implemented.
- [ ] Candidate cleanup implemented.
- [ ] Legacy composite/category cleanup retired.
- [ ] Full automated and large-library acceptance completed.

## Final outcome

Implementation is in progress. Step 1 accepted ADR 0020 and marked the one-scan
composite design transitional. Step 2 added versioned workflow phases and policy
bindings to authoritative library state, atomic manifest checkpoint publication,
conservative migration for missing/incompatible workflow data, restart replay,
serialized session advancement, and uncertain-state precedence. Five focused
Domain tests and 16 focused Application/Infrastructure state tests pass. UI behavior
and the Exact cleanup algorithm remain unchanged through this slice. Step 3 added
explicit `ExactOnly`, reserved `CandidateResidual`, and transitional
`FullCompatibility` analysis modes. Exact-only analysis performs catalog/path/full
hash processing and Exact grouping, emits count/timing metrics, bypasses all
EPUB/PDF/metadata/matching/recommendation work, and publishes its initial baseline
atomically at `ExactReady`. The 18 scan-use-case tests and 8 state-session tests
pass. Step 4 configured the production WPF workflow for exact-only analysis,
exposed `Exact cleanup` and `Candidate cleanup` as the primary toolbar actions,
gated Exact cleanup on current authoritative `ExactReady` state, advanced completed
and nothing-to-do results to `CandidatePreparationReady`, restored gating after
restart, disabled repeat Exact cleanup, and withheld legacy Metadata/Expanded/
composite mutation context from staged generations. Candidate preparation remains
explicitly deferred to later plan steps. The unchanged Exact executor's 11 tests
and the 16 focused WPF workspace/window tests pass. The tested nothing-to-do UI
path requires the existing backup acknowledgement, starts no Calibre tool or
worker, advances the workflow checkpoint, and disables repeat Exact cleanup.
Final verification for steps 1-4 completed with successful restore, clean full
solution build, 570 passing tests, clean formatting, clean diff hygiene apart from
Git line-ending notices on two Markdown files, and no vulnerable direct or
transitive packages. `ExecuteBulkExactDuplicateCleanupUseCase` has no diff, and no
step-5 reconciliation or later unified-candidate implementation was introduced.
Step 5 added a pure catalog reconciliation policy that replays only contiguous
typed Exact deltas, validates the projected inventory, classifies unchanged,
transferred, expected-removed, targeted-hash-required, and unexplained
associations, and fails closed on every unexpected record, metadata, format,
stored-name, identity, or delta change. Its 10 synthetic Application tests pass.
Step 6 retains the exact-analysis baseline and completed hash-chained Exact deltas
across normal checkpoint compaction, rereads the catalog read-only, resolves every
current association through the existing path boundary, reuses only explained
unchanged fingerprints, target-hashes transfer destinations, rejects mismatches,
and atomically publishes a new all-physical generation with exact source
provenance. Step 7 carries reusable EPUB/PDF assessments across exact-only
generations, rebinds compatible facts by fingerprint/version to current records and
paths, inspects only misses, and delegates candidate-only EPUB signatures to the
existing no-prose fingerprint/version cache. Cancellation or any preparation
failure leaves the completed Exact generation authoritative. Final hardening adds
read-only physical probes for unchanged files, explicit transfer-source proof,
target fingerprint verification, contiguous retained-delta validation, and
compare-and-swap publication against the exact source generation/revision. Missing,
locked, unsafe, changed, stale, unproven, or incomplete evidence fails closed before
publication. Final verification passes with 604 tests, clean formatting and diff
hygiene, no vulnerable direct or transitive packages, and the Exact cleanup
executor unchanged.
Step 8 adds versioned unified candidate groups, evidence provenance, advisory
classification, review findings, deterministic existing-policy keeper ranking,
and a complete-component merge policy. Exact Metadata groups are mandatory inputs
outside the ordinary candidate cap; a focused 25-member test proves they remain
present. Language, series/index, strong-identifier, edition-marker, and content
contradictions prevent unsafe bridges, while snapshot invariants enforce disjoint
current-record membership. Seven focused Domain tests pass.
Step 9 adds cancellable residual analysis over the refreshed physical generation:
exact Metadata detection runs independently of Expanded candidate caps, existing
Expanded discovery and fingerprint-keyed content caching are reused, unified groups
are built only after both evidence streams complete, and compare-and-swap
publication advances the same generation to `CandidateAnalysisReady`. Expanded
limit outcomes retain uncapped Metadata candidates. Four focused orchestration
tests and 18 state/serialization tests pass; cancellation and stale publication
leave the refreshed generation authoritative.
Step 10 adds persisted unified group/member presentation with virtualized grids,
keeper-first title and author columns, evidence/classification/review details,
session-scoped Skip and keeper overrides, deterministic ranking facts, and safe
viewer launch. Keeper changes notify title, authors, record ID, and every affected
member action. Unified groups and exact-source provenance round-trip through the
state store. Candidate cleanup remains non-executing: no Candidate request,
planner, worker orchestration, or mutation command was added.
Final steps 8-10 verification passes with a clean full build, clean formatting and
diff hygiene, no vulnerable direct or transitive packages, and 626 passing tests.
The complete suite is run with project-level parallelism disabled because unchanged
PDF/viewer worker tests can contend for process resources when all test projects run
concurrently. The Exact cleanup executor and post-Exact refresh implementation were
not modified by these slices.
