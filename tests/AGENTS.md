# Test Project Instructions

These instructions extend the repository root `AGENTS.md`.

Use xUnit, FakeItEasy, and FluentAssertions.

- Prefer behavior-oriented test names and Arrange/Act/Assert.
- Keep tests deterministic and offline.
- Never use the user's real Calibre library.
- Use temporary directories and synthetic fixtures.
- Assert safety properties, not only outputs.
- Verify cancellation for long-running operations.
- Verify malformed inputs produce findings or controlled errors.
- Avoid testing private implementation details.
- Add architecture tests for dependency rules.
- Use realistic ebook samples only where focused unit tests are insufficient.
