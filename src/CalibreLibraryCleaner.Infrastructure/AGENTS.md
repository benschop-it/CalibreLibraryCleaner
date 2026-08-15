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
- External process execution must capture safe technical identity, exit code,
	bounded output, duration, and failure outcome without logging book metadata.
- Escape command-line arguments safely.
- Mutate only through the fixed typed persistent `calibre-debug` worker; never add
	direct SQLite/filesystem mutation or a fallback engine.
- Implement versioned hash/assessment/signature/provider/model caches so corruption
	or incompatibility becomes a miss.
- Keep HTTP/provider and model-runtime details behind Application-owned ports.
- Tests must use temporary directories and synthetic fixtures, never a real user library.
