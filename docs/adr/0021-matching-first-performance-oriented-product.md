# ADR 0021: Prioritize Matching Quality, Caching, and Simple Backup-Assumed Cleanup

- Status: Accepted
- Date: 2026-08-15
- Supersedes as target architecture: ADR 0012's mandatory detailed projected-state persistence
- Amends: ADR 0017, ADR 0019, ADR 0020

## Context

The staged Exact and unified Candidate workflow has completed large-library
acceptance. The persistent worker can process thousands of mutations, while EPUB/PDF
assessment and content matching dominate cold analysis time. Earlier designs spent
substantial effort on cancellation, immutable execution/recovery models, detailed
journals, rollback, and repeated byte verification. The product now assumes that the
user maintains a complete external library backup before cleanup.

The highest-value remaining work is better same-work detection and faster repeated
analysis. Exact metadata matching misses variants; current EPUB content evidence
improves recall but does not cover PDF-only records, visual editions, weak metadata,
or external bibliographic identity. Full SHA-256 reads on every Exact scan also
prevent the application from realizing the value of stable file identity and caches.

## Decision

### Product priority

Optimize in this order:

1. precision and recall for same-work-and-language matching;
2. cold/warm throughput, cache reuse, bounded resources, and visible progress;
3. explainability, keeper override, and Skip;
4. simple supported Calibre mutation.

Cancellation, resumability, rollback, and recovery convenience are not product
optimization goals. Existing behavior may remain where it supports safe resource
cleanup, but future work should not expand those systems by default.

### Matching semantics

One executable Candidate group represents records believed to contain the same work
and language. Different editions, revisions, illustrations, or formatting may remain
in one group. This is an intentional high-throughput policy, not a claim that files
are interchangeable. Every group exposes evidence and contradictions, starts
included, and is processed unless the user selects Skip or changes the keeper.

Future matching may combine:

- deterministic local metadata, identifiers, series, language, binary relations,
  EPUB/PDF content, cover, and structure evidence;
- configured online bibliographic providers, enabled by default, with provider,
  request-field, response, cache, and version provenance; and
- versioned local ML/embedding evidence.

Provider failure must not block local matching. Cloud AI is not part of this
decision. No matching evidence directly starts mutation.

### Performance and caches

Caches are first-class product infrastructure. Cache identity includes stable input
identity and all relevant algorithm/model/resource versions. A later scan may reuse a
SHA-256 digest when canonical path, size, timestamps, attributes/file identity, and
cache provenance are unchanged. Selective or periodic byte revalidation detects
cache drift. A user-requested verification scan may force full hashing.

Recompute only affected records, candidate neighborhoods, signatures, assessments,
and groups where practical. Measure cold and warm behavior separately.

### Progress and cancellation

Long operations must keep the UI responsive and visibly active with truthful phase,
units, and elapsed time. Cancellation is not a product requirement. Existing
cancellation may remain best effort, but new work does not need arbitrary interruption
or resumable partial analysis. Incomplete analysis is not published.

### Mutation and recovery

Every mutation run requires explicit confirmation that a complete external backup
exists. The application does not create, verify, or restore that backup. The fixed,
typed persistent `calibre-debug` worker remains the only mutation engine; direct
SQLite and direct managed-file mutation remain prohibited.

Stop on failed or ambiguous mutation, log technical context without book content,
and require an explicit Rescan before further mutation. Automated rollback/recovery
is not a product feature. The long-term state target is minimal durable run/workflow
status sufficient to prevent accidental continuation. The current generation,
checkpoint, marker, journal, typed-delta, and uncertainty implementation remains the
baseline until a separate simplification plan safely removes it.

## Consequences

- Matching work may deliberately favor recall and throughput over edition
  preservation; backup, evidence, keeper override, and Skip are the controls.
- Online metadata use becomes normal configured behavior and must be transparent.
- Warm scans should become substantially faster than cold scans.
- Full rehashing is no longer a permanent product requirement.
- Progress is mandatory; cancellation and resume are optional.
- Future features should not expand rollback/recovery or detailed mutation-state
  complexity.
- ADR 0012 remains useful implementation history but not the target persistence
  architecture.
- ADR 0019's local deterministic matcher is the implemented baseline, not the limit
  on future evidence sources.
- ADR 0020's staged Exact-first sequence remains accepted; its requirement to hash
  every file on every Exact scan is amended by versioned hash reuse.

## Rejected alternatives

- Keep investing primarily in rollback, recovery, and arbitrary cancellation.
- Restrict executable groups to proven identical editions.
- Require manual opt-in for every uncertain Candidate group.
- Keep all matching offline and deterministic indefinitely.
- Enable cloud AI as a mutation authority.
- Trust a cache without identity/version checks or periodic validation.
- Replace the fixed Calibre worker with direct SQLite or filesystem writes.
