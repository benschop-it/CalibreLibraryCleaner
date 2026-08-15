# ADR 0015: Use a Persistent Calibre API Mutation Worker

- Status: Accepted, mutation scope amended by ADR 0020
- Date: 2026-08-03
- Amends: ADR 0003, ADR 0007, ADR 0013, ADR 0014

> Current interpretation: the fixed typed worker and operation set remain current.
> ADR 0020 reuses the same worker for Unified Candidate cleanup as well as Exact
> cleanup; it does not grant any additional mutation engine or arbitrary operation.

## Context

Exact-duplicate cleanup currently launches `calibredb` once and durably rebuilds projected state and WPF presentation for every logical mutation. A measured 100-operation segment took 135.5 seconds, and approximately 5,887 projected mutations took hours.

`calibredb` has no persistent command-stream mode for `add_format` or `remove_format`. Calibre documents a thread-safe Python database `Cache` API with `add_format`, batched `remove_formats`, and batched `remove_books`, and `calibre-debug` can execute an application-owned script inside the installed Calibre runtime.

## Decision

The default exact-duplicate cleanup engine is one persistent `calibre-debug` worker per cleanup. The worker uses Calibre's documented database API through one open `Cache`. This is supported Calibre tooling; it is not permission for direct SQLite access or a general embedded Python boundary.

Application owns a versioned typed batch protocol. Infrastructure owns the fixed worker script, trusted process launch, executable and script identity, JSON-lines serialization, bounded I/O, timeout handling, and Calibre API capability handshake.

The worker may perform only these exact-cleanup operations:

- transfer one fingerprint-verified source format to an unambiguous target without replacement;
- remove an allow-listed set of formats through one `remove_formats` call per chunk; and
- remove allow-listed empty records non-permanently through one `remove_books` call per chunk.

One request contains at most 100 logical operations. Transfers precede dependent source removals, and record removals remain last. The worker verifies typed postconditions through the same Cache before acknowledging a chunk.

There is no fallback mutation engine. Worker discovery or handshake failure stops before mutation. After a worker starts mutation, any crash, timeout, protocol violation, malformed response, or unverifiable result is logged, marks projected state uncertain, stops execution, and requires explicit Rescan.

The compatible range remains `9.11.0 <= version < 10.0.0`. Discovery additionally probes `calibre-debug`, the protocol version, library identity, and required API methods. Calibre 10 or later requires a new compatibility decision.

Calibre GUI, Calibre server, and other known library writers must remain closed throughout a worker run. The application does not terminate those processes.

## Consequences

- Thousands of process launches become one process launch and one Calibre cache initialization.
- Native format and record removals are batched.
- Complementary transfers do not require application-local staging.
- Direct SQL, direct managed-file mutation, arbitrary scripts, shell invocation, and non-Calibre Python remain prohibited.
- One bounded run marker and complete-chunk batch projection preserve durable typed state without per-operation flush cost.
- Ambiguous chunk failure remains conservative and requires Rescan.

## Rejected alternatives

- Continue launching one `calibredb` process per logical operation.
- Run commands through a shell or a general Python installation.
- Mutate `metadata.db` or Calibre-managed files directly.
- Use `calibre-server` as a process-start workaround.
- Retry a partially executed worker chunk through `calibredb`.
- Allow concurrent Calibre GUI or third-party writers.