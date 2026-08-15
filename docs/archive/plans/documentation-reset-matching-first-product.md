# Documentation Reset for Matching-First Product Direction

## Objective

Replace the accumulated milestone-era documentation with a concise, internally
consistent description of the product as implemented today and the accepted target
direction. Make matching quality, throughput, caching, and visible progress the
primary engineering goals. Treat a complete external library backup as a mandatory
operating precondition; do not design an application rollback/recovery product.

## Scope

- Rewrite the authoritative product, requirements, architecture, domain, matching,
  scoring, safety, testing, roadmap, and repository overview documents.
- Add one ADR recording the current product decisions and superseding outdated
  target assumptions around full rehashing, cancellation, and durable mutation state.
- Update root and test AGENTS instructions so future work follows the new priorities.
- Add a documentation index that clearly separates current truth, accepted ADRs,
  current work, workflows/evidence, and historical material.
- Move all completed plans and milestone handoffs to `docs/archive/` while keeping
  this reset plan active under `docs/plans/`.
- Describe implemented capabilities separately from accepted but unimplemented work.
- Reconcile stale references after archival.

## Out of scope

- Changing production code, matching algorithms, caching behavior, persistence, or
  mutation execution in this task.
- Deleting ADR history.
- Implementing online providers, embeddings, PDF content fingerprints, or hash-cache
  reuse.
- Removing current durable state before a separate implementation plan and tests.
- Running another destructive library acceptance exercise.

## Relevant requirements

Product decisions confirmed on 2026-08-15:

- Executable Candidate groups represent the same work and language; edition,
  revision, illustration, or formatting differences do not automatically split a
  group.
- Published Candidate groups are processed unless the user explicitly skips them.
- Matching may use deterministic local evidence, configured online bibliographic
  providers enabled by default, and local ML/embeddings. Cloud AI is not part of
  the accepted direction.
- Later scans may reuse SHA-256 when stable file identity is unchanged; selective or
  periodic validation protects cache quality.
- Cancellation is not a product requirement. Long operations must remain responsive
  and visibly active, but may complete their current bounded unit and need not be
  restartable from partial analysis.
- Every mutation run assumes and confirms a complete external backup. Automated
  rollback/recovery is not required.
- Target mutation persistence is minimal run status plus mandatory Rescan after a
  failed or ambiguous run. Current projected-state machinery remains implemented
  until explicitly simplified.
- Completed plans and handoffs should be aggressively archived.

## Existing implementation inspected

- Staged Exact-only analysis, Exact cleanup, trusted residual refresh, Candidate
  analysis, and unified Candidate cleanup are implemented and accepted.
- Exact analysis currently hashes every resolvable format; compatible EPUB/PDF
  assessments and EPUB signatures are cached and reused.
- Current matching is local, deterministic, author-indexed, bounded, language-aware,
  and uses metadata/identifier/series/binary evidence plus candidate-only EPUB
  content signatures.
- PDF assessment is isolated and bounded, but PDF cross-document matching is absent.
- The fixed persistent `calibre-debug` worker is the only mutation engine.
- Current state persistence uses generations, workflow checkpoints, typed deltas,
  markers, journals, and uncertainty blocking. This exceeds the newly accepted
  minimal target but is the implemented baseline.
- Progress, caching/reuse, same-scale staged workflow, worker execution, and cleanup
  have measured acceptance evidence.
- Automated recovery, application-created backup bundles, general cleanup plans,
  and legacy category mutation surfaces were removed.

## Proposed design

### Authoritative documentation set

`docs/README.md` becomes the entry point. Current truth is limited to:

- `product-vision.md`
- `functional-requirements.md`
- `architecture.md`
- `domain-model.md`
- `duplicate-detection.md`
- `quality-scoring.md`
- `safety-and-rollback.md` (retained path, reframed as operating safety)
- `test-strategy.md`
- `roadmap.md`
- accepted ADRs, especially the new matching-first ADR

Plans describe active work only. Workflows retain reusable procedures and measured
acceptance evidence. Archived plans/handoffs are explicitly non-authoritative.

### Current versus target language

Every core document distinguishes:

- **Implemented now:** behavior present in production and verified.
- **Target direction:** accepted future behavior not yet implemented.
- **Remaining work:** prioritized slices needed to reach the target.

No document may describe target hash reuse, online enrichment, local embeddings,
or minimal mutation state as already implemented.

### Product priorities

Use this order when tradeoffs conflict:

1. matching precision/recall for same-work-and-language grouping;
2. throughput, incremental caching, bounded resource use, and progress visibility;
3. explainability and user ability to override/skip;
4. mutation simplicity through supported Calibre tooling;
5. defensive recovery/cancellation convenience.

Safety invariants remain: no direct database writes, no managed-file mutation during
analysis, fixed worker-only mutation, explicit backup confirmation, no continuing or
retrying an ambiguous mutation, no book-content logging, and Rescan before another
mutation after failure.

### Matching direction

The roadmap advances evidence in layers: richer normalization and labeled
calibration; PDF fingerprints; cover/structure evidence; content-language
classification; configured bibliographic enrichment; local embeddings; evidence
fusion and confidence calibration. Same-work/language grouping optimizes recall but
retains explicit evidence, contradictions, keeper override, and Skip.

### Performance direction

Version caches by input fingerprint/identity and algorithm/model/resource versions.
Reuse hashes for unchanged stable file identity, with selective/periodic byte
validation. Avoid whole-library work when only affected records/evidence need
recomputation. Measure cold/warm scans, cache hit rates, candidate-generation cost,
parser cost, memory, log volume, and mutation throughput on disposable libraries.

## Files expected to change

- `README.md`
- `AGENTS.md`
- `tests/AGENTS.md`
- `docs/README.md` (new)
- all nine core documents listed above
- `docs/adr/0021-matching-first-performance-oriented-product.md` (new)
- status metadata in directly amended ADRs if needed
- `docs/archive/README.md` (new)
- all current files under `docs/plans/` except this plan (moved)
- all files under `docs/handoffs/` (moved)
- stale links in current documentation and archived Markdown

## Safety considerations

- The rewrite must not weaken the implemented worker-only mutation boundary.
- Assuming a backup does not authorize direct SQLite or filesystem mutation,
  continuing ambiguous operations, or deleting unique formats without the selected
  same-work/language keeper policy.
- Minimal mutation state is target work, not permission to delete current state code
  without a separate implementation plan.
- Online providers must have documented provenance, bounded requests/cache, safe
  failure behavior, and a clear disclosure of transmitted metadata. Provider failure
  must not block local matching.
- Local embeddings remain versioned evidence and cannot directly invoke mutation.
- Archived material remains available for historical rationale but is explicitly
  non-authoritative.

## Implementation steps

1. Add ADR 0021 with the confirmed decisions and supersession relationships.
2. Rewrite product vision and functional requirements around matching and speed.
3. Rewrite architecture, domain, matching, scoring, safety, and test strategy with
   implemented/target distinctions.
4. Rewrite roadmap into completed baseline, prioritized next work, optional later
   work, and removed/superseded work.
5. Rewrite README and add `docs/README.md` navigation.
6. Align root/test AGENTS instructions with progress-not-cancellation, backup-assumed
   mutation, matching quality, caching, and measured performance.
7. Move completed plans/handoffs into `docs/archive/` and add archive guidance.
8. Repair current and archived links; search for stale recovery/cancellation/full-
   rehash claims and classify intentional historical references.
9. Review the full documentation diff against current code and accepted decisions.

## Tests

Documentation verification consists of:

- all required plan sections present;
- current docs contain no duplicate paragraphs or contradictory current claims;
- current docs distinguish implemented and target behavior;
- no broken relative links in current docs;
- archived documents are labeled non-authoritative through their directory index;
- accepted ADR statuses/supersession references are coherent;
- AGENTS instructions do not require cancellation/recovery work contrary to product
  direction;
- searches confirm removed systems are not described as current;
- `git diff --check` and editor diagnostics are clean.

Production tests are not required because this task changes documentation only.

## Verification commands

```powershell
rg -n "rollback|recovery|cancel|always hashes|full hash|backup bundle|calibredb" README.md AGENTS.md docs --glob "!docs/archive/**"
rg -n "docs/plans/|docs/handoffs/" README.md AGENTS.md PLANS.md docs --glob "!docs/archive/**"
git diff --check
```

## Risks

- Simplifying prose can accidentally erase an implemented safety invariant.
- Describing same-work/language cleanup too casually can hide edition-loss tradeoffs;
  the backup assumption and Skip/override controls must remain explicit.
- Online-by-default evidence creates privacy/network expectations; transmitted fields
  and provider provenance must be documented before implementation.
- Physical archival can break old links; run link searches and update references.
- Current code may temporarily exceed the target architecture; docs must name that
  gap rather than misrepresent implementation.

## Unresolved questions

None. Product choices required for this reset were confirmed by the user.

## Progress

- [x] Core docs, ADRs, plans, handoffs, AGENTS, and current implementation audited.
- [x] Matching, caching, cancellation, mutation-state, evidence-source, and archival
  decisions confirmed.
- [x] Documentation reset plan written.
- [x] ADR 0021 added.
- [x] Core documents rewritten.
- [x] AGENTS instructions aligned.
- [x] Historical plans/handoffs archived.
- [x] Links and contradictions reviewed.

## Final outcome

Documentation reset completed on 2026-08-15. ADR 0021 records the confirmed
matching-first, performance-oriented, backup-assumed direction. README, all core
docs, workflows, PLANS, and repository/nested AGENTS now distinguish the implemented
staged baseline from target hash caching, richer evidence, online providers, local
models, incremental recomputation, and minimal mutation state.

All 29 prior plans, three milestone handoffs, and the obsolete bootstrap workflow
were moved under `docs/archive/` without deleting their historical content. Current
documentation links pass the relative-link check. Searches confirmed removed
cleanup-plan/recovery behavior is described only as historical, removed, or an
explicit non-goal. An independent review found no safety weakening; its useful
clarifications about staged production configuration, hash-cache absence, read-only
evidence tabs, PDF worker isolation, and cancellation scope were incorporated.

No production code or runtime behavior changed. `git diff --check` and editor
diagnostics are clean apart from Git line-ending notices on existing Markdown files.
