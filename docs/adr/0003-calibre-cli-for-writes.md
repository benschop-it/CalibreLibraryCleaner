# ADR 0003: Use Supported Calibre Tooling for Mutations

- Status: Accepted
- Date: 2026-07-11

## Context

Future cleanup needs to add/replace formats, update metadata, remove records, merge content, and restore backups. Direct database/filesystem mutation is unsafe.

## Decision

Execute approved plans through supported Calibre command-line tooling where possible, behind one Infrastructure boundary.

## Required behavior

Revalidate preconditions, back up and verify first, capture Calibre version and command output, safely escape arguments, record exit status, reload the library, verify results, and persist an audit record.

## Consequences

Calibre maintains consistency, but external processes and version compatibility add failure modes and testing needs.

## Rejected alternatives

Direct SQLite writes and GUI automation are rejected. Embedding Calibre's Python internals is deferred because of packaging and compatibility complexity.
