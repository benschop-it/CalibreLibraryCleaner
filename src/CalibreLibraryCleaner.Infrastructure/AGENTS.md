# Infrastructure Project Instructions

These instructions extend the repository root `AGENTS.md`.

- Implement interfaces declared in Application.
- Open `metadata.db` read-only and never execute SQL write statements.
- Do not mutate Calibre-managed files during analysis.
- Validate and canonicalize paths; prevent unintended traversal outside the selected library root.
- Stream large files and use bounded concurrency.
- Treat malformed EPUB/PDF content as findings, not application crashes.
- Wrap third-party exceptions at clear boundaries with useful context.
- Use structured logging.
- External process execution must capture executable version, arguments, exit code, stdout, stderr, duration, and cancellation.
- Escape command-line arguments safely.
- Never invoke destructive Calibre commands without a validated cleanup plan.
- Verify backups before mutation and verify the resulting library state afterward.
- Tests must use temporary directories and synthetic fixtures, never a real user library.
