# Implement Feature Workflow

## Before editing

1. Read root and applicable nested `AGENTS.md` files.
2. Read `docs/roadmap.md` and feature-relevant specifications.
3. Read accepted ADRs.
4. Inspect existing code and tests.
5. Create or update an execution plan when the task is substantial.

## Implementation rules

- Preserve every safety invariant.
- Never write directly to `metadata.db`.
- Keep dependencies within documented project boundaries.
- Use asynchronous APIs and propagate cancellation.
- Add or update xUnit tests with FakeItEasy and FluentAssertions.
- Add structured logging at integration boundaries.
- Avoid unrelated refactoring and future-roadmap implementation.
- Update documentation when externally visible behavior changes.

## Before finishing

- Run formatting, build, and relevant tests.
- Review the complete diff.
- Confirm no safety rule was weakened.
- Summarize changed files, assumptions, risks, and remaining work.
