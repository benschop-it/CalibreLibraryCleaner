# Safety and Operating Assumptions

The filename is retained for compatibility. The product does not provide rollback or
recovery.

## Backup assumption

Every Exact or Candidate mutation run requires explicit confirmation that the user
has a complete external backup of the selected Calibre library. The application does
not create, inspect, verify, restore, or manage that backup.

If cleanup produces an unacceptable or uncertain result, the user restores the
external backup outside the application and performs an explicit Scan.

## Analysis safety

Analysis may:

- open `metadata.db` read-only/query-only;
- read, hash, and inspect Calibre-managed formats;
- use configured online bibliographic providers;
- run bounded local models; and
- write application-owned state, caches, metrics, and logs outside the library.

Analysis must not:

- write directly to `metadata.db`;
- rename, move, overwrite, or delete Calibre-managed files;
- create files inside the selected library;
- execute ebook actions, follow links, run OCR implicitly, or fetch resources
  referenced from untrusted ebooks; or
- log book content or ordinary bibliographic metadata by default.

Untrusted EPUB/PDF input remains bounded by documented file, archive, XML/HTML,
object, stream, page, memory, CPU, and wall-time limits.

## Single-writer assumption

Calibre, calibre-server, ebook editors, synchronization tools, and unrelated library
writers remain closed while the cleaner mutates the selected library. Analysis and
cache reuse may rely on stable file/catalog identity under this assumption, but path
containment and input validation remain mandatory.

## Matching and cleanup tradeoff

A Candidate group means same work and language, not necessarily the same edition,
revision, illustrations, or formatting. Published groups are processed unless the
user selects Skip. The generated keeper can be changed.

This policy can intentionally remove distinct editions from the working library.
The external backup is the recovery mechanism. Matching evidence, contradictions,
coverage, provider/model provenance, and advisory confidence remain visible so the
user can intervene efficiently.

No matching, online, ML, or AI component directly invokes mutation.

## Mutation safety

Only the fixed persistent `calibre-debug` worker may mutate a library. It uses a
fixed typed protocol and supported Calibre database APIs. Direct SQLite writes,
direct managed-file mutation, shell commands, arbitrary scripts, GUI automation,
and mutation-engine fallback are prohibited.

Operations are deterministic and ordered:

1. transfer selected complementary formats;
2. remove non-keeper/source formats; and
3. remove records proven empty.

The keeper's existing same-format file wins. Non-physical, missing, unsafe, stale,
or unverified groups are skipped before mutation.

## Failure handling

Preflight or worker-start failure stops before mutation.

After mutation begins, any failed, ambiguous, interrupted, or unverifiable worker
outcome:

1. logs technical run/chunk/operation identifiers and failure codes without book
   content;
2. stops without retrying, continuing, inferring a successful prefix, or switching
   engines; and
3. blocks further mutation until explicit Rescan.

The current implementation marks detailed projected state uncertain and retains
markers/deltas/checkpoints. The accepted target may simplify this to minimal durable
run/workflow status, but stop-and-Rescan behavior remains.

## Progress and cancellation

The UI must remain responsive and show active phase, truthful units, and elapsed time
throughout long work. Cancellation is not required. If retained, it is best effort
at safe boundaries. Partial analysis is discarded; partial mutation follows the
failure rule above.

## Caches

A cache may improve performance but cannot silently bypass identity/version checks.
Hash reuse requires unchanged stable file identity and provenance. Selective or
periodic byte validation detects drift; a verification scan can force full hashing.
Cache corruption or loss becomes a miss, not invented evidence.

## Online providers and local models

Configured online providers are enabled by default. Settings disclose providers and
transmitted field categories. Requests are bounded and cached with provenance.
Provider/network failure falls back to local matching.

Local model evidence is versioned and bounded. Provider/model output is evidence,
not authority. Credentials, payloads, embeddings, and book metadata are not written
to ordinary logs.

## Explicit non-features

- application-created backup bundles;
- undo;
- automated rollback;
- automated recovery/reconciliation;
- resumable partial analysis as a requirement;
- automatic retry of mutation; and
- a direct-command or direct-database fallback.
