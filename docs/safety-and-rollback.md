# Safety and Failure Handling

## Analysis mode

Analysis may read the Calibre database and managed files, hash and parse formats, and create application-owned reports or development cache artifacts outside the library.

The supported operating model is single-writer for the selected library while CalibreLibraryCleaner is running: no unrelated process modifies the library, and Calibre changes occur only when initiated through the cleaner's controlled worker. The application may optimize redundant race-defense checks around this assumption. It still performs initial containment/reparse validation, current full-file SHA-256, bounded untrusted-format parsing, and projected-state validation because those establish identity and safety rather than merely detect unrelated writers.

Analysis must never write directly to `metadata.db`, rename or move Calibre-managed files, modify metadata, add or remove formats, or create files inside the selected library. EPUB and PDF inputs remain untrusted and are processed through the documented bounded read-only inspection paths. Book content is not logged.

Staged exact-only analysis still resolves and SHA-256 hashes every current format,
but does not inspect EPUB/PDF content or generate candidate evidence. Post-exact
Candidate preparation may reread the catalog and target-hash explained affected
associations. It may reuse a fingerprint only from authoritative pre-exact identity
plus a completed typed mutation relation; timestamp, size, path, stored name, or
record ID alone never proves identity. Any unexplained catalog or file change stops
preparation and requires a new exact analysis.

Explicit duplicate-row viewing may launch the trusted Calibre `ebook-viewer.exe`. The requested format must be a physical regular file contained in the selected library. Launch uses no shell and passes the physical path as one argument. Viewer launch does not alter projected state or authorize cleanup.

## Persisted development state

A successful explicit Scan creates one authoritative projected-state generation outside the library. During development, explicit Load may restore that state or migrate a strictly validated legacy snapshot without rescanning. This deliberately trusts development cache data and does not detect external Calibre changes.

Startup listing reads only small manifests or bounded legacy metadata. A manifest references one active baseline/checkpoint and one active delta journal. New manifest publication is atomic and precedes deletion of unreferenced state files. Shutdown does not compact or write a large snapshot.

## Duplicate cleanup

Exact duplicate, exact-metadata candidate, and content-confirmed expanded candidate cleanup are the only mutation workflows. Before each run, the user confirms that a complete external backup of the Calibre library exists. The application does not create, inspect, or verify that backup.

The workflow:

1. requires authoritative projected state;
2. deterministically builds keeper, transfer, format-removal, and empty-record-removal operations;
3. discovers and validates the trusted Calibre installation;
4. acquires one library mutation lease;
5. opens one fixed persistent `calibre-debug` worker;
6. writes one bounded cleanup-run marker;
7. sends typed chunks of at most 100 operations;
8. projects and durably journals only complete successful chunks; and
9. writes one final checkpoint after complete success.

Transfers precede dependent source removals. Formats are removed before records, and a record is removed only when projected empty. Exact cleanup leaves ambiguous or conflicting records unchanged. Metadata cleanup is explicitly keeper-authoritative: same-format alternatives on Remove records are deleted even when not byte-identical, while unresolved complementary sources skip the group before mutation.

Expanded cleanup accepts groups from authoritative matching evidence. A deterministic quality policy proposes one keeper; every group starts unskipped, and the user may select another sole keeper or Skip the group. `Cleanup eligible` and `To be reviewed` communicate evidence strength only and do not change execution behavior. Complementary formats are transferred, then all formats and empty non-keeper records are removed. Candidate evidence may not prove identical work, edition, revision, illustrations, or formatting; this limitation is repeated in the backup confirmation. Expanded cleanup does not rewrite title, authors, or other metadata.

Metadata cleanup may follow exact cleanup without a rescan. Exact cleanup clears generated recommendations and may add authoritative `ProjectedPresent` formats to its keeper. In that state, metadata cleanup derives a fallback keeper from available present/projected format coverage and metadata quality, accepts projected formats only on the keeper, and requires every Remove-source format to remain scan-observed `Present`. Complementary transfer without a recommendation is allowed only for one unique present source or byte-identical alternatives; conflicting alternatives still skip the group.

The staged target runs Exact cleanup first through the existing algorithm and fixed
worker path unchanged. Candidate cleanup remains disabled until Exact cleanup
completes successfully, including a successful nothing-to-do result. The two stages
are separate mutation runs and each requires explicit confirmation of a complete
external library backup. Candidate preparation and Candidate mutation are separate
user activations so the generated keeper, evidence, and Skip state can be reviewed.

## Cleanup ordering

Cleanup workflows never execute a frozen plan from the initial scan. Every run validates the current UI selections against the latest authoritative projected state. Exact groups are recomputed from projected fingerprints, and exact-metadata groups are filtered as records disappear. Therefore exact cleanup may be followed by metadata cleanup without a rescan.

Expanded matching evidence is derived from bounded content inspection and is discarded after any standalone mutation rather than projected as still current. Therefore the three category-specific cleanup commands cannot be chained in arbitrary order from one initial scan.

`Cleanup all` remains a transitional one-scan workflow while staged Candidate
cleanup is implemented and proven. It must not mutate a generation owned by staged
workflow state. Standalone Metadata/Expanded cleanup and composite cleanup are
retired only in a later parity slice; until then their existing safety rules remain
in force.

The worker uses only Calibre's documented database `Cache` API through the fixed embedded script and strict JSON-lines protocol. Direct SQL, shell invocation, direct managed-file mutation, arbitrary Python, GUI automation, direct `calibredb` mutation commands, and mutation-engine fallback are prohibited.

## Failure handling

Preflight, discovery, lease, worker startup, or handshake failure logs structured technical context and stops before mutation.

A failed, ambiguous, interrupted, unpersistable, or unprojectable mutation:

1. logs the run ID, safe operation identifiers, worker failure code, and exception details without book content;
2. stops immediately;
3. does not retry, continue, invoke another engine, or infer a successful operation prefix;
4. marks projected state uncertain/Rescan-required; and
5. blocks later mutation until an explicit successful Scan creates a new generation.

Cancellation before mutation is ordinary cancellation. After mutation starts, cancellation is observed only between worker chunks and follows the same uncertain-state rule if the outcome cannot be proven complete.

## Restoration

Automated rollback and recovery are not provided. If manual restoration is needed, the user restores from the externally maintained full-library backup using Calibre-supported procedures outside this application, then performs an explicit Scan before further cleanup.

## Testing

Automated destructive tests use only synthetic or caller-marked disposable libraries. They never discover or mutate a default or personal Calibre library. Tests cover deterministic operation order, backup acknowledgement, lease exclusion, worker-only execution, bounded chunks, complete-chunk projection, structured failure logs, state uncertainty, restart replay, checkpointing, and explicit-Scan recovery of application state.
