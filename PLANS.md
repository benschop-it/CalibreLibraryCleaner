# Execution Plans

Create an execution plan for work that spans projects, changes architecture, introduces an external dependency, affects cleanup safety, or requires multiple implementation steps.

Store active plans under `docs/plans/` using descriptive names such as:

```text
docs/plans/improve-pdf-matching.md
```

## Required plan structure

```markdown
# Plan title

## Objective
## Scope
## Out of scope
## Relevant requirements
## Existing implementation inspected
## Proposed design
## Files expected to change
## Safety considerations
## Implementation steps
## Tests
## Verification commands
## Risks
## Unresolved questions
## Progress
## Final outcome
```

Keep the plan current during implementation. Record deviations and failed approaches. Prefer small vertical slices over broad speculative scaffolding.

After implementation, verification, and documentation reconciliation are complete,
move the plan to `docs/archive/plans/`. Archived plans are historical and no longer
define current requirements.
