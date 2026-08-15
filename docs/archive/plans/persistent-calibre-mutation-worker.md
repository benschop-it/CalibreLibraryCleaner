# Persistent Calibre Mutation Worker and Batched State Updates

## Objective

Reduce an approximately 6,000-operation exact-duplicate cleanup from hours to under 15 minutes by reusing one trusted Calibre process and batching mutation, state persistence, projection, and presentation work.

## Scope

- A versioned Application-owned batch mutation contract.
- One persistent `calibre-debug` process per bulk cleanup.
- Calibre `Cache` API format transfer, format removal, and empty-record removal.
- Chunks of at most 100 logical operations.
- Pre-mutation `calibredb` fallback when the worker is unavailable.
- Batched projected-state persistence and WPF presentation in later slices.
- Performance instrumentation and disposable-library qualification.

## Out of scope

- Direct SQLite or Calibre-managed filesystem mutation.
- Calibre 10 or later.
- Concurrent Calibre GUI, server, or third-party writers.
- Automatic retry or resume after a worker may have mutated the library.
- Recovery workflow migration in the first worker slice.

## Relevant requirements

- Use supported Calibre tooling for all mutations.
- Never rescan during cleanup or recovery.
- Durably project every successful typed mutation.
- Mark state uncertain after any ambiguous, interrupted, unpersistable, or unprojectable mutation.
- Preserve unique formats and keeper overrides.
- Keep the normal workflow one-click.

## Existing implementation inspected

- `ExecuteBulkExactDuplicateCleanupUseCase` starts one `calibredb` process and commits one state delta for every logical operation.
- `DirectCalibreProcessRunner` safely invokes one process but has no persistent session.
- `LibraryStateSession.ApplyAsync` and `VersionedJsonLibraryStateStore.AppendDeltaAsync` project, flush, and publish each delta independently.
- `MainWindowViewModel` rebuilds the complete presentation after every `StateChanged` event.
- A measured 100-operation segment took 135.5 seconds, approximately 1.36 seconds per mutation.

## Proposed design

Application owns typed worker session, chunk, operation, and result contracts. Infrastructure validates and launches `calibre-debug.exe` adjacent to the trusted `calibredb.exe`, deploys a fixed application-owned script, clears the process environment, performs a bounded JSON-lines handshake, and keeps one Calibre `Cache` open for the run.

Operations are dependency ordered: transfers, format removals, then empty-record removals. Each request contains at most 100 logical operations. The worker reports ordered typed results and verifies postconditions through its open Cache before acknowledging the chunk. Failure before a mutation starts may fall back to the existing CLI gateway. Failure or malformed communication after mutation starts marks projected state uncertain and stops the run.

Later slices add durable chunk intent/commit records, indexed batch projection, one manifest flush per chunk, end-of-run compaction, and suppressed WPF presentation refreshes during execution.

## Files expected to change

- Application execution contracts and bulk cleanup use case.
- Infrastructure Calibre worker, deployment, discovery, and dependency injection.
- Projected-state session/store and Domain batch projection.
- WPF state publication and performance presentation.
- Application, Infrastructure, Architecture, and WPF tests.
- Active architecture, safety, requirements, and test documentation.

## Safety considerations

- The worker runs only under the installed, capability-probed Calibre runtime.
- The script and executable identities are validated before mutation.
- No SQL or direct managed-library file writes are used.
- The Calibre GUI and other known writers must remain closed for the complete run.
- Mutations are serial even when planning or staging is parallelized.
- A worker crash or ambiguous response after mutation starts requires explicit Rescan.
- A partially executed worker chunk is never retried through `calibredb`.

## Implementation steps

1. Add ADR 0015 and this execution plan.
2. Add versioned batch worker contracts and deterministic validation.
3. Add the deployed Python worker and persistent Infrastructure client.
4. Route exact cleanup through one worker session with pre-mutation CLI fallback.
5. Add focused protocol, lifecycle, ordering, fallback, and uncertainty tests.
6. Add batch state intent/commit persistence and replay.
7. Add indexed batch projection and one state publication per committed chunk.
8. Suppress per-operation WPF presentation rebuilding.
9. Add instrumentation and synthetic/real-Calibre performance qualification.
10. Update architecture and safety documentation and run full verification.

## Tests

- Protocol rejects unsupported versions, oversized chunks, malformed formats, invalid IDs, and out-of-order results.
- One worker process handles the complete cleanup.
- Transfers complete before source removals.
- Format and record removals are natively batched.
- Startup/handshake failure falls back before mutation.
- Crash or malformed output after mutation marks state uncertain and does not fall back.
- Cancellation stops at a safe chunk boundary.
- Batch projection is equivalent to sequential delta application.
- A 20,000-book/6,000-operation fixture causes at most 60 commits and one final UI publication.
- Opt-in disposable Calibre 9.11 and 9.12 tests verify close/reopen inventory.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check HEAD
```

## Risks

- Calibre's documented Python API may change within a supported major version; handshake capability probes fail closed.
- A process can fail after applying part of a native batch; write-ahead chunk intent and uncertainty preserve safety.
- Calibre writers outside this application can invalidate the worker cache; startup and between-chunk writer checks are required.
- The first worker slice removes process-launch overhead but does not achieve the target until state/store/UI batching lands.

## Unresolved questions

- None for the initial worker slice. Chunk size is fixed at 100 and the performance target is under 15 minutes.

## Progress

- [x] Performance bottlenecks measured and design agreed.
- [x] Execution plan and ADR written.
- [x] Persistent worker contracts and initial implementation complete.
- [x] Bulk cleanup uses one worker session with pre-mutation CLI fallback.
- [x] Durable write-ahead chunk intents and uncertain restart replay implemented.
- [x] State/store projection, journal append, manifest update, and final checkpoint are batched.
- [x] WPF presentation updates are deferred to one final state publication.
- [ ] Performance target qualified.

## Final outcome

Exact-duplicate cleanup now defaults to one persistent, capability-probed `calibre-debug` worker. Worker operations are ordered in chunks of at most 100, guarded by durable write-ahead intents, projected through an indexed one-pass batch projector, hash-chain appended with one fsync and manifest update per chunk, published to WPF once, and compacted once after successful completion. CLI fallback is available only before mutation for explicitly classified availability failures; trust failures and ambiguous worker outcomes fail closed.

Synthetic verification proves 200 mutations produce two worker chunks, two intents, two batch commits, one checkpoint, and one final state event. A 10,000-book/6,000-delta Domain fixture projects in one batch. A disposable real Calibre 9.x smoke transferred a complementary format, removed two source formats, removed the empty source record, closed the worker, and verified the final inventory. The complete suite passes 612 tests with 2 existing opt-in real-Calibre qualification tests skipped.

The remaining work is qualification of the under-15-minute target against an approximately 20,000-book disposable library with roughly 6,000 real Calibre mutations and recorded phase timings.