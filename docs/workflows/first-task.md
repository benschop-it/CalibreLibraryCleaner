# First Codex Task in Rider

After opening the extracted folder in Rider, select Codex in AI Chat and use this prompt:

```text
Read AGENTS.md, PLANS.md, docs/roadmap.md, docs/architecture.md,
docs/test-strategy.md, and all accepted ADRs.

Prepare an execution plan for Milestone 0 only.

Do not create product functionality yet.

The result must establish:
- a .NET 10 solution;
- the four production projects described in the architecture;
- the four test projects described in the architecture;
- central package management;
- nullable reference types;
- analyzers and formatting configuration;
- project references enforcing the dependency direction;
- dependency injection and structured logging packages where appropriate;
- xUnit, FakeItEasy, and FluentAssertions;
- architecture tests enforcing project boundaries;
- a successful restore, build, format verification, and test run.

Create the plan under docs/plans/.
Before editing code, list every proposed file and explain why it is needed.
```

After reviewing the plan, use:

```text
Implement the approved Milestone 0 execution plan.

Do not implement Calibre database access, duplicate detection,
EPUB analysis, PDF analysis, cleanup execution, or functional WPF screens yet.

Run restore, build, formatting verification, and all tests.
Then review the complete diff against AGENTS.md, PLANS.md,
the architecture document, and the accepted ADRs.

Report files changed, commands run, test results, assumptions,
remaining risks, and any deviations from the approved plan.
```
