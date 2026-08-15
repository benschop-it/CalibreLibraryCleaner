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
and superseded decisions. ADRs under [adr/](adr/) preserve decision history. The current product direction is
[ADR 0021: matching-first, performance-oriented product](adr/0021-matching-first-performance-oriented-product.md).
Earlier ADRs remain useful implementation/history records but are superseded or
amended where ADR 0021 says so.

## Current work

Active execution plans live under [plans/](plans/). A plan is not authoritative
product behavior until its outcome is implemented, verified, and reflected in the
core documents above.

## Workflows and evidence

Reusable procedures and measured acceptance evidence live under
[workflows/](workflows/). They support, but do not override, the current core docs
and accepted ADRs.

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
