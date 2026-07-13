# Calibre Library Cleaner — Rider and Codex Starter

This folder contains the initial specifications and Codex instructions for starting Calibre Library Cleaner in JetBrains Rider.

## Start

1. Extract the ZIP into an empty project folder.
2. Open that folder in Rider.
3. Open Rider AI Chat and select Codex.
4. Ask Codex to read `AGENTS.md` and the documentation.
5. Use the prompt in `docs/workflows/first-task.md`.
6. Review the proposed execution plan before allowing code changes.

## Important files

- `AGENTS.md` — repository-wide Codex instructions.
- `PLANS.md` — execution-plan requirements.
- `docs/roadmap.md` — milestone order and scope.
- `docs/architecture.md` — project boundaries.
- `docs/safety-and-rollback.md` — non-negotiable safety model.
- `docs/adr/` — accepted architectural decisions.
- nested `AGENTS.md` files — project-specific instructions.

## Initial MVP

The first useful version is read-only:

- select and validate a Calibre library;
- open `metadata.db` read-only;
- read books, authors, identifiers, and formats;
- report missing files;
- calculate SHA-256 hashes;
- group exact normalized title/author duplicates;
- assess EPUB quality;
- display explainable recommendations;
- make no library changes.
