# Application Project Instructions

These instructions extend the repository root `AGENTS.md`.

- Depend only on Domain.
- Own use cases and interfaces implemented by Infrastructure.
- Do not reference SQLite, WPF, concrete filesystem APIs, ebook parser implementations, or process-launching implementations.
- Keep each use case focused on one user-visible outcome.
- Propagate cancellation through all asynchronous operations.
- Return structured findings for expected problems instead of throwing generic exceptions.
- Validate input at use-case boundaries.
- Preserve deterministic ordering where results are displayed or serialized.
- Do not implement UI concerns or external-process details.
