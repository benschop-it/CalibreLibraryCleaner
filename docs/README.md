# Documentation

## Current authoritative documents

Read these first:

1. [Product vision](product-vision.md)
2. [Functional requirements](functional-requirements.md)
3. [Architecture](architecture.md)
4. [Duplicate detection and matching](duplicate-detection.md)
5. [Domain model](domain-model.md)
6. [Quality scoring and keeper ranking](quality-scoring.md)
7. [Safety and operating assumptions](safety-and-rollback.md)
8. [Test strategy](test-strategy.md)
9. [Roadmap](roadmap.md)

## Decisions

The [ADR index](adr/README.md) classifies current, amended, implementation-baseline,
and superseded decisions. ADRs under [adr/](adr/) preserve decision history.
[ADR 0021](adr/0021-matching-first-performance-oriented-product.md) records the
matching and performance baseline, but its broad future priorities are amended by
the current one-time cleanup product decision and active finish plan. Earlier ADRs
remain useful implementation/history records where they do not conflict with that
decision.

## Current work

Active execution plans are listed in the [plans index](plans/README.md), including
current provider/review follow-up work. The completed
[high-quality library disposition plan](archive/plans/high-quality-library-and-unresolvable-disposition.md)
records generated mixed-library WPF acceptance and real-parser measurements.
The completed
[metadata cache compatibility fix](archive/plans/reuse-compatible-metadata-cache-after-author-display-fixes.md)
records legacy reuse, preserved author-order coverage and measured zero-request
first-reuse/warm verification.
The completed
[bounded metadata regex fix](archive/plans/eliminate-bounded-metadata-regex-timeouts.md)
records linear-time execution and unchanged matching/author decisions without
short wall-clock timeouts.
The completed
[read-only timeout boundary fix](archive/plans/contain-read-only-regex-timeouts.md)
records command failure containment, retained display/state safety and explicit
retry verification.
The completed [selected quarantine/repair workflow fix](archive/plans/scope-identity-repairs-to-selected-books.md),
[Calibre author storage decoding fix](archive/plans/decode-calibre-author-storage-names.md)
and [author inspection fact reuse](archive/plans/reuse-compatible-author-inspection-facts.md)
record the subsequent verified UI, copy-precondition and measured reuse corrections.
The completed
[final author resolution and managed naming plan](archive/plans/final-author-resolution-and-managed-naming.md)
is archived with its implementation and acceptance evidence.
The completed one-time cleanup plan is under [archive/plans](archive/plans/).

## Workflows and evidence

Reusable procedures and measured acceptance evidence live under
[workflows/](workflows/). They support, but do not override, the current core docs
and accepted ADRs.

The accepted deterministic package and destructive workflow evidence are recorded in
[Windows release acceptance](workflows/windows-release-acceptance.md).

## Historical material

Completed milestone plans and handoffs live under [archive/](archive/). They explain
how the repository evolved and may intentionally describe removed systems. They are
not current requirements or architecture.

## Conflict order

When documents conflict, use this order:

1. current user/product decision;
2. newest accepted ADR;
3. current core documents listed above;
4. active execution plan for the requested work;
5. archived plans/handoffs.
