# Decode Calibre author storage names

## Objective

Prevent reviewed quarantine copies from falsely reporting changed author metadata
when the catalog reader exposes SQLite's pipe-escaped author spelling rather than
the public Calibre API spelling.

## Scope

Decode author-name storage escaping at the read-only SQLite boundary. Preserve
credit order, sorts, IDs and all other catalog values. Verify actual Calibre
quarantine copy with comma-containing coauthors and retain all failure safeguards.

## Out of scope

Renaming authors, changing matching policies, retrying the failed live move,
projecting a copy prefix, deleting destination copies, restoring user selections,
weakening metadata checks or writing to the user's library.

## Relevant requirements

- [Safety](../../safety-and-rollback.md): exact current input verification, fixed worker
  and stop/Scan after ambiguous mutation.
- [Architecture](../../architecture.md): Infrastructure translates integration storage
  details; Domain values must match supported API semantics.
- Current user request: fix the failed quarantine move without more blind retries.

## Existing implementation inspected

Technical log records 284 selected source records, first copy chunk failed with
`quarantine_source_metadata_changed`, zero committed copy chunks and no source
deletion. Destination copies may nevertheless exist inside the failed chunk.
The user voluntarily closed the application afterward.

The SQLite reader copies `authors.name` verbatim. Calibre stores commas in this
column as pipes, but its API exposes comma spellings. Read-only comparison of
17,183 source records found 36 raw author mismatches, zero title or ordered-sort
mismatches, and zero author mismatches after pipe-to-comma decoding. No book files
were opened for that metadata diagnosis. The old log does not identify which
selected record failed; this is a demonstrated contract defect affecting those
records, not a guessed external edit.

## Proposed design

Replace storage pipes with commas only when constructing a catalog author record.
Do not split authors, reorder credits, change sorts, or normalize bibliographic
identity. The worker continues comparing exact public API values to the reviewed
expectation. A fresh explicit Scan is still required for the already uncertain
production state; do not make stale snapshots authoritative through migration.

## Files expected to change

SQLite reader, focused reader regressions, generated actual-worker acceptance and
current documentation.

## Safety considerations

This is decoding, not mutation or fuzzy matching. Keep source/destination identity,
format fingerprints, copy/read-back/delete ordering, backup confirmation and
failure stop behavior. Do not retry the user's failed operation or infer source
deletions from committed chunk counts.

## Implementation steps

1. Reproduce escaped author names leaking from the SQLite reader.
2. Decode at the producer boundary and preserve all other values/order.
3. Exercise real generated comma-author quarantine copy and actual metadata API.
4. Finish queued normal UI/build/full verification after voluntary app closure.

## Tests

Single/multiple encoded authors, already decoded/plain names, credit order/sorts
and byte-preserving read-only behavior. Real generated Calibre library must
demonstrate stored pipes/public commas and complete verified quarantine copy/delete
without relaxed checks. Full matching/author/quarantine suites preserve behavior.

## Verification commands

Focused Infrastructure reader tests, actual generated Calibre worker acceptance,
normal Debug build/full solution tests, format/whitespace and dependency audit.

## Risks

Already persisted snapshots contain storage spellings and cannot silently become
valid. Changed query inputs may miss affected cache identities, but no broad
proposal/provider/query version bump or cache deletion is warranted.

## Unresolved questions

The failed production chunk's precise record is absent from the log. Do not claim
that a particular record was identified. The storage/API mismatch itself was
verified read-only across the current source catalog.

## Progress

- [x] Verified the public API/storage mismatch with zero library writes.
- [x] Four generated reader regressions failed before the fix and passed afterward,
  preserving credit order/sorts/IDs and the exact database bytes.
- [x] Fixed only the read-only producer boundary; no worker comparison weakening.
- [x] Real Calibre 9.12.0 generated two-comma-coauthor acceptance completed verified
  copy/delete under protocol 1.7 and confirmed raw storage escaping/public spelling.
- [x] Copy failure issues now include the failed record ID when reported by the
  worker, without paths/metadata or inferred chunk-prefix commits.
- [x] Actual generated worker acceptance passed with comma-containing coauthors.
- [x] Normal Debug build and full solution verification completed on 2026-10-29:
  Domain 517, Application 357, Infrastructure 391, Architecture 34, WPF 108;
  all 1,407 passed, zero skipped. Formatting, whitespace and dependency audit passed.
- [x] Reconciled current documentation and archived this completed fix.

## Final outcome

Completed and verified. Catalog authors now use the same comma spelling as the
supported Calibre API, preserving IDs, order and sort values. The worker's exact
source expectation was not weakened and no production mutation was retried.

The failed production run reported no source deletion. Read-only destination
diagnosis found 41 existing records, all with source UUIDs still present; its
database predated the failed run, so do not claim all 41 were created by that
attempt. No destination cleanup or inferred copy-prefix recovery was performed.

The already uncertain saved source state requires explicit Scan, not Load, before
another mutation. Compatible caches remain intact. Existing destination records
must be considered before retry to avoid duplicate copies; do not silently reuse,
overwrite or remove them.
