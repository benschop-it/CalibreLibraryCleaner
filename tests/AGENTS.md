# Test Project Instructions

These instructions extend the repository root `AGENTS.md`.

Use xUnit, FakeItEasy, and FluentAssertions.

- Prefer behavior-oriented test names and Arrange/Act/Assert.
- Keep tests deterministic and offline.
- Never use the user's real Calibre library.
- Use temporary directories and synthetic fixtures.
- Prioritize labeled matching quality, evidence contradictions, group determinism,
	cache identity/invalidation, and cold/warm work counts.
- Assert mutation/analysis safety properties, not only outputs.
- Verify progress for long-running operations. Verify cancellation only where the
	behavior remains implemented or is required to release resources safely; do not
	add new cancellation points solely for test coverage.
- Verify malformed inputs produce findings or controlled errors.
- Mock online providers and local model runtimes in the ordinary suite; never depend
	on live network/model downloads.
- Avoid testing private implementation details.
- Add architecture tests for dependency rules.
- Use realistic ebook samples only where focused unit tests are insufficient.
