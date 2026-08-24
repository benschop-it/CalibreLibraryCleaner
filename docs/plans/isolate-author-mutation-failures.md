# Isolate author mutation failures

## Objective

Allow all unrelated Candidate and metadata operations to complete before risk-prone
author writes, while making any final ambiguous author failure immediately diagnosable.

## Scope

- Execute author metadata writes after all non-author operations.
- Put each author write in its own worker chunk.
- Log exact failed operation, record, field, source, and global/chunk position.
- Refine author readback failure codes without changing the fail-closed boundary.

## Out of scope

- Retrying or continuing after an ambiguous mutation.
- Automatic recovery, rollback, or direct Calibre database writes.

## Relevant requirements

Failed or unverifiable mutation stops and requires explicit Rescan. Complete successful
chunks are checkpointed, so safe ordering should maximize useful committed work first.

## Existing implementation inspected

Metadata fields currently execute per subject, placing Authors second. A failed author
readback can therefore block thousands of unrelated later fields. Worker logs include
chunk position and field but omit operation ID, record ID, global position, and source.

## Proposed design

Partition planned operations while preserving relative order: bounded non-author chunks
first, then one-operation author chunks. Keep the same durable mutation intent and delta
rules. Add structured failure identity and granular worker author-readback codes.

## Files expected to change

- Candidate cleanup orchestration and tests.
- Fixed Calibre worker and boundary tests.
- Current safety/architecture documentation.

## Safety considerations

No operation is retried. An ambiguous author write still stops immediately. Single-item
author chunks prevent an uncredited successful prefix and ensure all unrelated operations
were already committed before author mutation begins.

## Implementation steps

1. Add risk-aware chunk ordering and exact operation logging.
2. Add granular author readback failure codes.
3. Add focused tests and run standard verification.
4. Build and smoke-test the package.

## Tests

- Non-author metadata runs before Authors.
- Every Authors chunk contains one operation.
- Failure log contains global/chunk position, operation ID, record ID, field, and source.
- Existing ambiguous failure still marks state uncertain and stops.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

## Risks

- More worker round trips for author-heavy runs.
- An early author failure still blocks later author-only writes, but no unrelated work.

## Unresolved questions

- Whether later Calibre qualification supports safely classifying any canonicalized
  author mismatch as a verified typed outcome.

## Progress

- [x] Current failure and execution ordering inspected.
- [x] Risk-aware ordering and logging implemented.
- [x] Granular worker failure codes implemented.
- [x] Verification and package smoke passed: 832 tests, formatting, diagnostics,
  diff check, vulnerability audit, worker syntax check, and packaged launch smoke.

## Final outcome

Implementation complete. All non-author work now checkpoints before author metadata,
each author write has an isolated chunk, and structured logs identify a known failed
operation exactly. Package SHA-256:
`22aadee312ab8573c8d0f7ea2fbf641beb6b5795cda655f9ae4d86403497e3bc`.
Live acceptance requires an explicit Scan of the uncertain library before retrying.