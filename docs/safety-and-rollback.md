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

Metadata provider annotations are not approved author replacements. The shared
author-write policy withholds the whole provider author field for recognized
pseudonym markers (including `psued.`), preserving all current names and per-author
sorts rather than guessing a replacement or dropping coauthors. This applies even
when the metadata row is checked and to final bibliographic-repair handoff.
Unrelated metadata may still apply; raw provider names remain labeled evidence,
not proposed writes. Existing annotated catalog names still require the separate
explicit Final authors cleanup. This does not change matching normalization.

Provider display preparation can invert explicit two-part family/given notation
and split bounded alternating pipe pairs into distinct credited authors. It does
not remove qualifiers or infer identities from familiar names. The prepared result,
not the raw provider casing, determines whether a merge is automatic; changing
order or credit count still requires explicit review. Complete ordered provider
credits, author display and author-sort values remain separate.

Provider-author preparation shares Final authors' deterministic initial typography:
each initial retains its dot and following space. Metadata review must not undo
an already canonical initial block. Normalizing two distinct provider credits to
the same display value withholds the whole author field, not a partial list.

The confirmed final mutation writes checked authors through the fixed worker, then
uses typed `update_path` for retained formatted records. Worker read-back must prove
two-level ID-suffixed containment, registered basenames/availability, unchanged format
bytes, format/record counts, and preserved metadata/local fields. Any mismatch is an
ambiguous mutation: stop immediately and require Rescan. Internal storage and
unregistered files are neither adopted nor moved.

Final quarantine is available only after that author/managed-name mutation succeeds.
Its read-only review covers every retained record, not only final-author exceptions.
The selection list is prepared from current metadata and existing evidence without
mandatory whole-library opening-page inspection. Lack of automatic title/byline
certification does not block explicit manual assignment and transfer of selected
books. It also never certifies unselected books or automatically assigns them.
Optional repair inspection is scoped to selected IDs; the actual move still checks
current selected-record inputs, backup confirmation and complete supported-API
copy/read-back before deleting any source.
Missing evidence, provider failure, a low score, or a placeholder-like title cannot
by itself qualify a book or select a default transfer. Explicit route coverage and
independent title/author evidence remain separate from confirmed mutation authority.
Reviewer acceptance confirms identity, content and disclosed companion defects; a
review exclusion alone is never acceptance. Completed moves with pending records
must not be described as a finished high-quality source library.

The default problem list is a presentation subset, not a new qualification policy.
Missing proof alone is not a diagnosed title/author defect, and hidden records remain
in retained-input accounting. Review-only warnings never automatically check a move.
Check all shown is an explicit manual assignment for the exact visible IDs, including
pending rows; hidden books are not assigned. The Move checkbox, selected/bulk
assignment and optional ID entry all use the same reviewed inclusion authority.
They change no Calibre files before the separate confirmed mutation.

Targeted identity repair reuses the existing metadata-only and final-author worker
actions, with separate backup confirmations and non-author-before-author ordering.
It does not create a new mutation engine. All final repair replacements start
unchecked, and changed inputs/evidence invalidate prior review. The application
does not blindly use a catalog ISBN to override contradictory local identity.
Its read-only query and proposal scope is the explicitly selected current records,
not every suspect record in the library. Empty/duplicate/unknown scope is rejected
before cache/provider I/O. Repairing one record must not erase unrelated saved
decisions, reset other quarantine choices or certify other records.

Its destination is the fixed same-parent sibling `<source name> - Unresolvable` and
must stay within Calibre's supported path bound. An existing destination is inspected
read-only, bound by UUID, backed up externally, and never reinitialized. Its existing
records and schema are preserved; equivalent metadata creates a separate duplicate
record. Missing or incompatible custom columns stop before source deletion.

Reviewed author expectations use the supported Calibre API spelling, not raw
SQLite comma-as-pipe escaping. The read-only catalog adapter performs this exact
decoding while preserving author order/sorts/IDs; the worker's exact metadata
comparison is not relaxed. Copy failure diagnostics identify a failed record
when the worker provides one. A failed chunk's destination copies remain ambiguous;
no source deletion or automatic retry follows from an inferred successful prefix.

The worker copies and exactly verifies all selected records before deleting any
source. Verification covers ordinary and local metadata, writable custom values,
derived composites, per-author sorts, covers, extra files, and exact format hashes.
Source deletion is then one record per worker chunk and projects only a verified
`RemoveRecordWithContentLibraryStateDelta`. A copy failure leaves every source record
untouched. A later delete failure may leave a verified deleted prefix and marks state
uncertain; explicit Rescan remains mandatory.

Because Calibre author entities are shared, final-author mutation may legitimately
change author display/sort metadata on records not directly selected in review. Before
quarantine review, the application rereads the catalog query-only and projects only
those verified author side effects. Library identity, record IDs, titles, managed
paths, identifiers, and registered formats must remain exact; any other drift stops
and requires Rescan.

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

Managed-name failures report the failed record ID and distinguish pre-read,
supported path update, cache clear, post-read and final verification failures.
Fixed categories plus bounded numeric OS errors distinguish a sharing violation
from an API/data error without exposing exception messages, filenames or metadata.
These diagnostics alone never authorize retry/skip/continuation. Earlier committed
chunks remain counted, but successful worker results inside a failed chunk do
not become an inferred committed prefix.

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

A managed-name rejection may also continue as a verified-unchanged skip. Read-only
Windows sharing probes reject already locked formats/covers/OPF/extra files before
Calibre can alter attributes. All probe handles are closed immediately; they never
rename, delete, write or repair a file. After a probe or supported-update rejection,
fresh cache-cleared read-back must exactly match a detached complete pre-state,
including managed path, ordered sorts, record timestamps/identity, registered
formats/names/paths/observations/hashes, cover/local/custom metadata, extras and
composites. Any change (including file attributes), unreadable state or failed
initial capture still stops. There is no update retry.

A proven naming skip is a typed no-op journal outcome, not verified naming evidence.
It preserves the record, remains explicitly pending at final qualification and is
reported with record ID/reason while other books continue. Metadata and naming skip
counts are separate. A skipped layout cannot become Qualified through the reviewer
acceptance command, nor automatically authorize an Unresolvable transfer.

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

Legacy metadata cache reuse requires recognized exact input/version identities and
current local validation of positive candidates. Promotion preserves the original
retrieval time, source versions, opaque source identity and unavailable retry age;
it cannot manufacture freshness or successful evidence. Unknown multi-author order
is explicitly carried through cache and fusion, never guessed from alphabetized
legacy credits. Author writes require independently established complete current
credit correspondence/order or preserved provider order. Unavailable order coverage
does not qualify a record for an automatic Unresolvable transfer.

Read-only UI actions contain regex timeout failures, display a technical error and
release their operation resources without automatic retry or successful fallback.
The error/log contains neither regex input nor pattern. A newly loaded authoritative
state cannot enable cleanup against older displayed results; reload or scan must
first present that state. The read-only error boundary never masks a mutation
exception or changes the existing stop/Rescan requirements.

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
