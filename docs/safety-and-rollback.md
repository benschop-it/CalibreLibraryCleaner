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

Registered format filename stems are preserved verbatim when appending their
validated extension. A space or dot immediately before that extension is valid
and must not cause a false InvalidPath classification or an analysis-time rename.
Directory components ending in spaces/dots, reserved device names (including a
space before the extension), traversal, invalid characters, and linked paths
remain rejected.

Interactive EPUB viewing never passes a Calibre-managed file to ebook-viewer.
Infrastructure copies the validated regular file to an opaque, size-bounded,
physical per-user staging directory outside the library, launches a dedicated viewer
instance, and deletes the copy when the viewer exits. Later launches prune a bounded
set of stale copies from abnormal termination. Staging paths and book bytes are not
logged. PDF viewing continues through the default Windows association after the same
contained-regular-file validation.

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

Candidate cleanup and metadata enrichment are separate deterministic mutation runs:

1. Candidate cleanup transfers selected complementary formats;
2. removes non-keeper/source formats; and
3. removes records proven empty.

Only after Candidate cleanup, an explicit online review resolves retained records.
A later confirmed metadata-only run applies checked fields/covers and verifies live
Cache read-back. No metadata operation is mixed with transfer/removal operations.

The keeper's existing same-format file wins. Non-physical, missing, unsafe, stale,
or unverified groups are skipped before mutation.

Metadata enrichment uses only checked, typed metadata-update operations through the
same worker. It validates current review/target identity, merges identifiers from the
live Cache, preserves local-only fields, uses supported Calibre APIs, and verifies
read-back after Candidate source records are already deleted. Title/author updates may move managed
paths; verified live paths are projected without direct filesystem mutation. Final
managed-name normalization remains a separate, explicit action and may use supported
Calibre behavior only.

Confidence alone does not authorize automatic metadata replacement. Automatic Apply
is limited to additive fields and bounded all-caps-to-mixed-case corrections.
Dropping current authors or replacing populated edition facts/same-type identifiers
requires explicit Apply. Empty or unavailable proposal fields do not clear current
values. Identifier updates merge by type and preserve unmentioned identifiers such
as Google IDs; an existing cover is never replaced. WPF's Final merged metadata
preview uses the same Domain field policy as execution.

Final author preparation is read-only and opens only exact-identity EPUB/PDF demand.
Verified online/current authors outrank embedded/front-matter evidence; unavailable,
limited, encrypted, conflicting, and role-ambiguous evidence remains non-executable.
Conservative family/given separator normalization retains exact per-author sorts and
may remove only an independently agreeing empty or strict life-date suffix.
Deterministic display typography may also default checked when it changes only NFC
representation, apostrophe style, or a bounded leading-initial block and returns exact
display/sort values without reordering, expansion, aliasing, or other qualifier
removal. Reviewed final-author writes may replace the old display-derived sort
precondition, but exact post-write display, per-author sort, and book-sort read-back
remains mandatory. All other corrections require an explicit checked review bound to
current generation, revision, title, ordered authors/sorts, formats, fingerprints, and
policy versions.

Pseudonym annotation removal is opt-in in Final authors, including for annotated
verified/current names. It preserves the published pen name, does not infer aliases
from the referenced real name, and does not derive identities from truncated folders.
Matching may ignore the same explicit annotation read-only before consolidation,
but original metadata remains available for review. Ambiguous or malformed markers
are flagged without automatically replacing the author. Only the existing confirmed
final-author worker action may persist a checked removal and refresh managed names.

The confirmed final mutation writes checked authors through the fixed worker, then
uses typed `update_path` for retained formatted records. Worker read-back must prove
two-level ID-suffixed containment, registered basenames/availability, unchanged format
bytes, format/record counts, and preserved metadata/local fields. Any mismatch is an
ambiguous mutation: stop immediately and require Rescan. Internal storage and
unregistered files are neither adopted nor moved.

Final quarantine is available only after that author/managed-name mutation succeeds.
Its destination is the fixed same-parent sibling `<source name> - Unresolvable` and
must stay within Calibre's supported path bound. An existing destination is inspected
read-only, bound by UUID, backed up externally, and never reinitialized. Its existing
records and schema are preserved; equivalent metadata creates a separate duplicate
record. Missing or incompatible custom columns stop before source deletion.

The worker copies and exactly verifies all selected records before deleting any
source. Verification covers ordinary and local metadata, writable custom values,
derived composites, per-author sorts, covers, extra files, and exact format hashes.
Source deletion is then one record per worker chunk and projects only a verified
`RemoveRecordWithContentLibraryStateDelta`. A copy failure leaves every source record
untouched. A later delete failure may leave a verified deleted prefix and marks state
uncertain; explicit Rescan remains mandatory.

## Failure handling

Preflight or worker-start failure stops before mutation.

Cover download is a bounded pre-mutation preparation step. Exhausted transient
timeout, transport, HTTP, or refused-extra-redirect outcomes may omit only the
affected cover before the durable intent is created; other approved metadata remains
eligible and the omitted count/reasons are reported without URLs or bibliographic
data. Unsupported endpoints/redirect targets, invalid response type/content/bounds,
unsafe paths, and local staging failures remain blocking. No untrusted redirect is
followed.

Successfully validated covers are atomically persisted in a separate bounded cache
before mutation. Cache identity includes the cache-policy version, source ID, URL,
maximum cover bytes, and maximum dimensions. Every hit revalidates physical paths,
manifest identity, byte bounds, JPEG dimensions, and SHA-256 before materializing a
copy in disposable worker staging. Corrupt, missing, incompatible, or unavailable
cache state is a miss. Capacity is bounded by both bytes and entry count, with oldest
entries removed first. The cache contains provider cover bytes and opaque identities;
ordinary logs contain aggregate hit/download/omission counts only.

After mutation begins, any failed, ambiguous, interrupted, or unverifiable worker
outcome:

1. logs technical run/chunk/operation identifiers and failure codes without book
   content;
2. stops without retrying, continuing, inferring a successful prefix, or switching
   engines; and
3. blocks further mutation until explicit Rescan.

To minimize this stop boundary's impact, author metadata writes execute only after
all non-author operations have completed and been checkpointed. Each author write is
its own worker chunk. A failed author readback therefore has no uncommitted successful
prefix and cannot block unrelated title, identifier, publisher, date, language,
cover, transfer, or removal work. Logs identify the exact global/chunk operation,
operation ID, record ID, field, provider version, expected value count, and granular
author readback failure category.

A verified-unchanged metadata rejection is not a failed mutation outcome. The worker
may continue only after comparing the complete post-operation target field, cover,
managed path, author sort, and protected local fields with their pre-write values and
proving equality. It emits a typed skipped result and no-op state delta. It does not
retry. Any changed or unreadable post-state follows the stop-and-Rescan rule above.

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
Hash reuse requires unchanged stable file identity and provenance; forced
verification at the Application boundary performs full hashing. Cache corruption or
loss becomes a miss, not invented evidence.

## Online providers and local models

Open Library same-work evidence is enabled by default. Read-only metadata providers
for Open Library and Google Books are implemented independently; Google Books runs
only when the user stores an optional current-user DPAPI-protected key. Settings
disclose that ISBN only, or title, author names, and optional language are transmitted.
Requests and responses are bounded and proposals are cached with provenance, without
the key. Provider/network failure leaves metadata unchanged and does not block the
other provider or duplicate cleanup.

Metadata review persists only bounded Apply overrides under an opaque library key.
Compatibility requires exact subject, fusion policy, provider/version/edition,
generation, and revision identity; stale decisions reset to safe confidence defaults.
The review decision store contains no API keys, raw provider payloads, bibliographic
metadata, covers, or library paths. Cover review currently shows availability and
bounded references only; it downloads nothing.

Configured local Ollama observations are versioned and bounded but do not affect
grouping or cleanup. Provider/model output is evidence or a review proposal, never
authority. Credentials, payloads, embeddings, and book metadata are not written to
ordinary logs.

## Explicit non-features

- application-created backup bundles;
- undo;
- automated rollback;
- automated recovery/reconciliation;
- resumable partial analysis as a requirement;
- automatic retry of mutation; and
- a direct-command or direct-database fallback.
