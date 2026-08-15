# ADR 0011: Use Automatic Format-Level Exact Deduplication

- Status: Accepted core Exact retention policy; execution mechanics superseded
- Date: 2026-08-02
- Supersedes: ADR 0010
- Amends: ADR 0007
- Amended by: ADR 0012

> Current interpretation: deterministic format-level retention and empty-record
> safety remain current. Cleanup-plan, direct `calibredb`, application backup, and
> fresh-scan-after-command mechanics below were replaced by ADRs 0012, 0013, 0015,
> 0017, and 0020.

## Context

A real library scan produced 5,754 exact-binary groups but only 119 exact metadata candidates. Requiring a user to select one keeper in every exact-file group is not feasible. More importantly, an exact match proves equality between two files, not equivalence between their Calibre records. Deleting a whole non-keeper record can discard additional formats, metadata, or covers that were not part of the exact match.

## Decision

Treat exact-binary cleanup as format deduplication, separate from later record merging.

For every completed fresh scan, generate retained-copy decisions for all exact-binary groups in deterministic group order. A group is eligible only when all members use the same canonical format label. Retain one copy using this ordered comparison:

1. the record containing the most formats;
2. the most complete usable stored metadata;
3. the greatest number of locally validated ISBN, DOI, ASIN, or OCLC identifiers;
4. presence of a cover; and
5. the lowest Calibre record ID only as a deterministic tie-breaker.

Selecting a row in WPF inspects it and does not change the generated decision. The proposal removes every other member with typed `calibredb remove_format`. A record may be removed with non-permanent `calibredb remove` only after all of its formats are planned for removal and a fresh post-format-removal scan proves that no formats remain.

The normal workflow is a library-wide proposal with eligible groups included by default and anomalous groups skipped. Immutable execution artifacts may be materialized in bounded slices, but every slice uses the same scan-wide deterministic decisions and becomes stale after any affected library state changes. Later record merging is a separate proposal that may transfer complementary formats and must not overwrite non-identical same-format files automatically.

## Safety rules

- A format removal requires a current retained association with the same canonical format, byte length, and SHA-256.
- Mixed-format-label exact groups are anomalous and skipped.
- Every affected record and format is backed up and independently verified before mutation.
- The approved plan explicitly lists format removals; record removals are derived, never independently selected.
- A record with any remaining format is preserved.
- Every command is validated against the current authoritative state revision and followed by a durably committed typed delta as defined by ADR 0012.
- Direct SQLite and managed-file writes remain prohibited.

## Consequences

Exact-file cleanup scales without making record equivalence claims and never discards an unrelated format merely because another format on the same record was duplicated. The workflow may leave multiple records representing the same book; resolving those records belongs to the separate merge phase.

The cleanup plan schema and policy advance to `exact-binary-cleanup-plan/2.0` and `exact-binary-cleanup-plan-policy/2.0.0`. The trusted Calibre profile must probe and allow-list `remove_format` in addition to export and non-permanent record removal.

## Rejected alternatives

- Keeping the first or lowest-ID member without ranking record quality.
- Requiring manual keeper selection for every exact-file group.
- Deleting every record except one merely because one format is byte-identical.
- Treating exact-file equality as proof that records are the same edition.
- Moving or merging complementary formats during exact-file deduplication.