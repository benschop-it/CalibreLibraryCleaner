# ADR 0016: Unify Persisted Development State

- Status: Accepted
- Date: 2026-08-05
- Amends: ADR 0012

## Context

Processing the development library takes approximately twenty minutes. Persisted analysis loading is therefore required for the edit/run/test loop. The application currently writes every successful scan twice: once as `.library-snapshot.json` and once as a state baseline. Old state generations also remain after later scans.

Startup originally deserialized the complete snapshot merely to list its library root. Metadata-only listing removes that immediate cost, but duplicate large artifacts and split persistence ownership remain.

## Decision

During development, persisted analysis loading remains supported and may be trusted by the developer without a fresh scan. This is a development acceleration mechanism, not a guarantee that external Calibre changes were detected.

The manifest-driven library-state store becomes the sole writer for new persisted scan results. Each library has one atomic manifest referencing one active baseline or checkpoint and one active delta journal. Startup listing reads only small manifests and never materializes the baseline, checkpoint, or journal.

Existing `.library-snapshot.json` files remain a temporary legacy input. They are listed through a bounded metadata reader. Explicit Load strictly validates the selected file, publishes it as a revision-zero state generation, and deletes it only after the state manifest is durably published.

After successful manifest publication, the store removes all unreferenced baseline, checkpoint, and journal artifacts for that library key. Failure to remove an unreferenced artifact is logged and retried after a later successful publication; it does not invalidate active state. The active manifest and its referenced files are never pruning candidates.

Checkpointing remains a normal state operation after successful cleanup and at the configured delta threshold. Application shutdown never performs checkpointing or another large-file write.

Removing persisted analysis listing/loading after development is explicitly deferred to a separate decision. The in-memory `LibrarySnapshot` analysis model is not temporary and is unaffected.

## Consequences

- Ordinary development changes can be tested without repeating a complete scan.
- New scans no longer produce two complete copies of the same analysis.
- Startup work scales with manifest count, not library size.
- Explicit Load still materializes one complete state and can remain expensive, but avoids scanning and reanalysis.
- Existing development caches migrate in place on first explicit Load.
- Only current-generation artifacts are retained after successful publication.
- A developer can load stale cached state by design and accepts that risk.

## Rejected alternatives

- Remove persisted loading before development is complete.
- Require a fresh scan after every code change.
- Keep writing both snapshot and state baseline formats.
- Compact all libraries while closing the application.
- Treat directory timestamps or filenames as sufficient listing metadata.