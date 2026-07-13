# ADR 0001: Read Calibre Directly, Never Write Its Database

- Status: Accepted
- Date: 2026-07-11

## Context

The application needs efficient analysis access to `metadata.db`, but direct writes risk desynchronizing Calibre's database and managed files.

## Decision

Read `metadata.db` through read-only SQLite access. Never issue database or schema mutations. Use supported Calibre tooling for library changes.

## Consequences

Analysis is efficient and safer, but the schema reader requires compatibility tests and write operations need a separate integration path.

## Guardrails

Enforce read-only connection mode, isolate schema mapping, add mutation-detection tests, surface unsupported schemas, and prohibit SQL write statements.
