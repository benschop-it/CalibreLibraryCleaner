# Architecture Decision Records

ADR bodies preserve the context and decision as written at that time. Use the status
and current-interpretation note before treating an older body as current behavior.
ADR 0021 is the current product-direction decision.

## Current decisions

| ADR | Classification | Current role |
| --- | --- | --- |
| [0001](0001-read-only-calibre-access.md) | Accepted | Read SQLite only; supported tooling for writes. |
| [0002](0002-clean-architecture.md) | Accepted | Domain/Application/Infrastructure/WPF boundaries. |
| [0003](0003-calibre-cli-for-writes.md) | Accepted principle, mechanics superseded | Supported Calibre tooling principle; ADR 0015 defines current worker. |
| [0004](0004-select-versone-epub-inspection-stack.md) | Accepted/amended | Current bounded EPUB stack; cancellation is not a product goal. |
| [0005](0005-versioned-recommendation-review-artifact.md) | Accepted analysis-only | Deterministic recommendation review export, no mutation authority. |
| [0009](0009-select-isolated-pdf-inspection-stack.md) | Accepted/amended | Current isolated PDF stack; cancellation is resource behavior, not product priority. |
| [0011](0011-automatic-format-level-exact-deduplication.md) | Accepted core policy | Exact retention/ranking; old plan/CLI execution mechanics superseded. |
| [0012](0012-persistent-delta-driven-library-state.md) | Implemented baseline, target superseded | Current detailed state; ADR 0021 targets simplification. |
| [0013](0013-one-click-exact-duplicate-cleanup.md) | Accepted/amended | Current Exact review/cleanup, staged by ADR 0020. |
| [0014](0014-calibre-9x-capability-compatibility.md) | Accepted/amended | Current 9.x range; worker handshake replaces old CLI capability list. |
| [0015](0015-persistent-calibre-api-mutation-worker.md) | Accepted/amended | Current fixed persistent worker for Exact and Unified Candidate cleanup. |
| [0016](0016-unified-development-state-persistence.md) | Implemented baseline | Current persistence/loading; ADR 0021 targets simpler mutation state. |
| [0017](0017-simplified-worker-only-cleanup.md) | Accepted foundation, scope amended | Worker-only, external backup, no recovery/fallback; ADR 0020 adds Candidate mutation. |
| [0019](0019-scalable-work-language-candidate-discovery.md) | Accepted matching baseline | Current bounded local matcher; ADRs 0020/0021 broaden group/evidence direction. |
| [0020](0020-staged-exact-first-candidate-cleanup.md) | Accepted/amended | Current production staged workflow; legacy mutation surfaces retired. |
| [0021](0021-matching-first-performance-oriented-product.md) | Accepted current direction | Matching/caching priority, same-work/language semantics, simple backup-assumed cleanup. |

## Superseded decisions

| ADR | Superseded by | Historical subject |
| --- | --- | --- |
| [0006](0006-immutable-cleanup-plan-artifacts.md) | ADR 0017 | General cleanup-plan artifacts. |
| [0007](0007-safe-calibre-command-execution.md) | ADR 0017; boundary evolved in ADR 0015 | Plan execution, backup bundles, journals. |
| [0008](0008-reconciliation-based-verified-recovery.md) | ADR 0017 | Automated recovery/reconciliation. |
| [0010](0010-separate-exact-binary-cleanup-plans.md) | ADRs 0011 and 0017 | Per-group Exact plans and backups. |
| [0018](0018-keeper-based-metadata-candidate-cleanup.md) | ADR 0020 | Standalone Metadata cleanup; semantics folded into Unified Candidate cleanup. |

## Decision precedence

When ADRs conflict, use the newest accepted/amending ADR. Current core documentation
linked from [docs/README.md](../README.md) explains the implemented baseline and
accepted target in consolidated form.
