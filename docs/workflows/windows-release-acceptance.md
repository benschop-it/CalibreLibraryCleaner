# Windows Release Package and Acceptance

## Deployment decision

The release is framework-dependent `win-x64`. On 2026-08-16, independently published
and collision-free WPF/PDF-worker payloads measured:

| Deployment | Files | Uncompressed size |
| --- | ---: | ---: |
| Framework-dependent | 79 | 23.18 MiB |
| Self-contained | 505 | 230.36 MiB |

The one-time application already requires installed Calibre. Requiring the .NET 10
Desktop Runtime x64 keeps the ZIP practical and avoids about 207 MiB of duplicated
runtime. WPF files remain at package root; the complete PDF worker publish remains in
`pdf-worker/` because flat merging produced a real same-named dependency collision.

## Prerequisites

- Supported Windows x64 (Windows 10 or Windows 11).
- .NET 10 Desktop Runtime x64.
- Calibre `9.11.0` or newer within the supported 9.x range; Calibre 10 is not accepted.
- Calibre, calibre-server, viewers, editors, and synchronization writers closed during
  mutation.
- A caller-selected disposable representative library, never a personal production
  library.
- A complete external backup explicitly confirmed immediately before destructive
  packaged acceptance.
- Live provider requests disabled unless the caller separately approves them. Use
  compatible cached proposals or controlled fake-provider evidence for acceptance.

## Build and automated verification

```powershell
.\scripts\Build-ReleasePackage.ps1 -Version 0.1.0
.\scripts\Test-ReleasePackage.ps1 -ZipPath .\bin\releases\CalibreLibraryCleaner-0.1.0-win-x64-framework-dependent.zip
```

The builder restores and publishes WPF and PDF worker independently. It does not rely
on the WPF post-build worker copy. It rejects symbols, tests, caches, credentials,
logs, library state, temporary cover staging, temporary files, and source-machine
paths. `release-manifest.json` lists every payload file except itself with normalized
relative path, byte size, and lowercase SHA-256.

The verifier rejects duplicate/unsafe ZIP entries, checks every manifest size/hash,
required runtime files, PE headers, dependency graphs, exclusions, and both .NET 10
runtime declarations. It
then runs `CalibreLibraryCleaner.Wpf.exe --release-smoke` directly from an extracted
package. That no-UI path verifies the complete PDF-worker layout and fixed embedded
Calibre mutation worker without initializing logs, state, providers, or child workers.
The smoke timeout is 15 seconds, above the measured 8,031 ms verification
time while remaining bounded on a slow workstation.

## Current package evidence

| Field | Actual value |
| --- | --- |
| Version | `0.1.0` |
| ZIP | `CalibreLibraryCleaner-0.1.0-win-x64-framework-dependent.zip` |
| Compressed bytes | 9,358,833 |
| Payload files in manifest | 79 |
| ZIP SHA-256 | `13a4e0590c4664ef3cb9ab5cec09da76498ecf42c8c8c6ec421d9df04e5e3dc8` |
| Reproducibility | Not rerun after the acceptance fixes; an earlier deterministic package reproduced across two complete builds |
| Direct packaged smoke | Passed |
| Smoke final state | 0 new processes; 0 new extraction directories |
| Live provider requests | Metadata review cache completion/retry accepted before this rebuild |
| Destructive library mutation | Accepted; final authoritative revision 1,840 |

## Interrupted acceptance attempt

The first packaged attempt used the caller-confirmed disposable library and current
external backup. Actual results before stopping were:

- Exact analysis: 27,952 books, 29,235 formats, 5,750 groups, 170,761 ms.
- Exact cleanup: 13,542 operations, 6,777 formats removed, 6,762 records removed,
  3 records merged, 1 skipped, 247,938 ms.
- Post-Exact catalog: 21,190 records; no worker/database lock; no cover staging residue.
- Local Candidate analysis: 21,190 records, 31 Exact metadata groups, 3,189 Expanded
  groups, 3,190 Unified groups, 129,882 ms.
- Deviation: the old package began Open Library enrichment before Unified cleanup.
  The user canceled; no Candidate or metadata mutation began, and no child process
  remained.

The workflow was corrected to `staged-cleanup/1.1.0`: Candidate preparation/load makes
zero edition-provider calls, Candidate cleanup advances to a post-cleanup checkpoint,
and online enrichment starts only through an explicit action for retained records.
The corrected package evidence above supersedes the old package hash.

During later visual review acceptance, cache reuse was confirmed but another defect
was observed: at 11,834 of 17,463 Open Library queries, 11,081 were cached and 752 were
online, then capped misses were rapidly counted as complete and the progress bar jumped
to 100%. The run was canceled with 12,319 cache entries retained. The resolver now
caches transient unavailable results for six hours, preserves immediate per-query
writes, stops at the true processed count on the request cap, and reports uncached
deferred queries so a later run can continue honestly from cache.

The next run completed online queries and retained 17,748 cache entries, then crashed
while displaying the new selected-proposal detail pane. Windows `.NET Runtime` event
1026 reported `InvalidOperationException`: a default TwoWay `TextBox.Text` binding
attempted to write the getter-only `Reasons` property. `Reasons` and `Provenance` are
now explicit OneWay bindings and the XAML architecture guard requires them. Exit-state
verification found no library change, cache temporary file, cover staging directory,
or child process.

The next corrected run loaded authoritative revision 7,813 at
`CandidateCleanupCompleted` and reconstructed 17,514 retained-record review subjects.
All 663 expired transient Open Library outcomes were retried: 9 became proposals, 2
ambiguous, 651 NotFound, and 1 remained unavailable. Review fusion produced 3,141
Medium proposals checked by default, 21 unchecked Low proposals, and 14,352
Unavailable subjects. No metadata mutation started.

Visual review then exposed a layout defect: the subject list, splitter, and selected
details used grid rows 1 through 3 while only rows 0 and 1 were declared, so the detail
pane covered the list. Separate list/splitter/detail rows now preserve at least 180
pixels for the virtualized list and allow the details pane to grow without a fixed
maximum. The 75-test WPF/architecture suite, package verifier, and direct smoke pass
for the current package above.

After review, all 21 Low proposals were explicitly selected, producing 3,162 checked
subjects with the 3,141 default-selected Medium proposals. The caller reconfirmed a
complete current external backup and authorized metadata mutation. The attempt stopped
during cover preflight with `CANDIDATE.METADATA_COVER_STAGING_FAILED` and updated zero
records. Revision 7,813 remained authoritative at `CandidateCleanupCompleted`; no
mutation marker, worker, Calibre process, staging residue, or cache temporary file was
created.

Anonymous endpoint checks showed valid Open Library cover URLs return either JPEG 200
or a two-redirect HTTPS chain: `covers.openlibrary.org` to one of two numeric
`archive.org` cover-archive paths, then to
`ia<number>.us.archive.org/view_archive.php`. The stager previously rejected the first
302. It now validates and follows only that bounded chain, preserving the original
numeric cover identity in the terminal `file` query and rejecting mismatched source
IDs, off-list destinations, and any additional redirect. All 10 focused cover tests
pass. The corrected package
above passes the package verifier and direct smoke; metadata retry remains pending.

The first redirect-capable package still stopped in cover preflight with the same
generic code and zero updates. Revision 7,813 again remained authoritative without a
pending marker, worker, child process, or staging residue. A bounded anonymous live
validation of all 2,019 distinct cached covers produced 2,017 immediate valid JPEGs,
one transient Internet Archive `502/303` outcome, and one timeout; both transient
covers later returned valid JPEGs. Cover staging now retries only timeout, transport,
`408`, `429`, and selected `5xx` responses, at most three attempts with one- then
two-second backoff. Deterministic trust, redirect-count, type, size, dimension, and
JPEG failures still stop immediately. Privacy-safe failure codes distinguish timeout,
HTTP, response validation, and local staging failures. The next package stopped
safely with `METADATA_COVER.RESPONSE_INVALID`, zero updates, and unchanged revision
7,813. This matched the observed intermittent extra redirect: exceeding the strict
two-hop limit now restarts from the original trusted URL without following the extra
destination, still bounded to three attempts. Failure results also carry only
aggregate completed/total counts for diagnosis. The full 776-test suite, package
verifier, and direct packaged smoke pass; metadata retry remains pending.

The `completed 0 of 2,029` request was reproduced directly from the persisted
authoritative review workspace without invoking Calibre. Its public Open Library
cover ID matched the initial URL, while Open Library selected a different internal
Internet Archive member ID with leading zeroes. The archive member filename and CDN
`file` query matched exactly, and the 50,347-byte JPEG passed the production dimension
parser. The stager now carries identity from that trusted archive member to the CDN
instead of requiring it to equal the public cover ID, and accepts a leading-zero
numeric member only when its parsed value is positive. The actual first checked cover
now stages successfully through the current implementation. Temporary diagnostic
source, solution entries, and the diagnostic PowerShell process were removed. The
full build, 776-test suite, package verifier, and direct packaged smoke pass; metadata
retry remains pending.

The next metadata attempt staged all 2,029 covers and started 20,206 metadata
operations. Four complete 100-operation chunks committed and the UI reported 400
updated fields before chunk 5 failed. Persisted state is uncertain at revision 8,213
with the mutation intent pending; no worker, Calibre child process, or cover staging
residue remains. Do not retry or infer a safe prefix; explicit Rescan is mandatory.

Read-only comparison against the intact revision-7,813 checkpoint proved the
reconstructed operation order matched all 400 committed technical operation IDs.
Within failed chunk 5, operation 412 was the first changed proposal absent from the
live catalog. It was a one-value language update whose normalized provider code is
not recognized by the platform ISO culture set; preceding metadata updates for that
subject persisted. The planner now omits unsupported provider language values and
preserves local language. Worker failures display/log the bounded technical code plus
committed/total operation counts, and WPF progress text displays exact counts. The
full build and 778-test suite pass. The corrected package passes its verifier and
direct packaged smoke; explicit Rescan remains pending.

The mandatory Rescan produced 17,514 records and no Exact groups. Exact NothingToDo
advanced cleanly; Candidate analysis produced 2 Exact Metadata, 75 Expanded, and 77
Unified groups. Reviewed Candidate cleanup completed 171 operations: 19 formats
transferred, 86 formats removed, 66 records removed, and 11 groups skipped. Online
review then produced 17,448 subjects: 3,134 Medium, 20 reviewed-and-selected Low, and
14,294 Unavailable.

The next metadata run staged covers and began 19,247 operations. Thirteen complete
chunks committed 1,300 metadata fields before chunk 14 failed with
`metadata_readback_failed`. State is uncertain at revision 1,471 with the mutation
intent pending; no worker, Calibre process, or cover residue remains. Do not retry or
infer a prefix; explicit Rescan is mandatory.

Read-only comparison against revision 171 exactly matched all 1,300 committed
technical operation IDs. Operation 1,353 (Title) was the last definite live change;
operation 1,354 (Authors) was the first changed field absent afterward. Proposed and
live author lists were equal in count, order, case, and content after whitespace
collapse, identifying Calibre author-whitespace canonicalization against the worker's
raw equality check. The worker now verifies ordered authors after whitespace
canonicalization but still rejects substantive changes. Installed `calibre-debug`
passed a synthetic in-memory qualification for both cases. Scan persistence now shows
explicit active feedback instead of leaving a premature completion message visible.
The full build and 779-test suite pass. The corrected package passes its verifier and
direct packaged smoke; explicit Rescan remains pending.

The next mandatory Rescan completed authoritatively with 17,448 books, one Exact file
group, and zero missing formats. Visual review exposed a committed regression from
`4dcbd9d`: splitter-style rows had been inserted into the Exact tab while the member
panel still used row 2, so selected members occupied only the 5-pixel splitter row.
The Exact review now restores three rows (`Auto / * / *`) and includes an XML structure
guard plus live XAML activation coverage. The full 780-test suite and standard checks
pass. The persisted ExactReady state may be loaded after restart without rescanning.
The corrected package passes its verifier and direct packaged smoke.

Packaged visual acceptance confirmed the selected Exact-group member panel is visible
and usable again, with nonzero measured bounds of 1,158 by 119 pixels. The
authoritative one-group ExactReady state remained unchanged; keeper/Skip review and
Exact cleanup remain pending.

Exact cleanup then completed two operations, removing one format and one empty record.
The next Candidate analysis produced 15 Unified groups. Reviewed Candidate cleanup
completed 11 operations (one transfer, five format removals, five record removals) and
safely skipped ten groups with incomplete physical facts. Online review then covered
17,442 retained records: 3,128 Medium, 19 reviewed-and-selected Low, and 14,295
Unavailable.

The next 19,207-operation metadata run committed 22 chunks and 2,200 fields before
chunk 23 failed with `metadata_readback_failed`. State is uncertain at revision 2,211
with a pending intent; no worker, Calibre child process, or staging residue remains.
Do not retry or infer a prefix; explicit Rescan is mandatory.

Read-only reconstruction from revision 11 exactly matched all 2,200 committed
technical operation IDs. Operation 2,236 was the boundary cover write: its managed
cover file was written during the run and exactly matched the expected
Calibre-normalized JPEG in a fresh session, while the next subject was untouched.
The failure was stale immediate `get_metadata(...cover_as_data=True)` projection. The
worker now clears that book's cache and verifies through `cache.cover(book_id)`.
Installed Calibre passed this postcondition against a caller-created temporary
synthetic library, and all temporary diagnostics were removed. Worker errors now
distinguish cover, field, and final-context readback codes. The full build and
780-test suite pass. The corrected package passes its verifier and direct packaged
smoke; mandatory Rescan remains pending.

The user approved continuing only for provably non-mutating metadata rejections. The
worker now snapshots the target field/cover, managed path, author sort, and protected
local fields before each write. Exact pre/post equality produces a typed skipped
result and persisted no-op delta; no retry occurs. Any changed or unreadable post-state
still stops as ambiguous and requires Rescan. Updated and verified-unchanged counts
are separate. Protocol `1.1`, real-Calibre unsupported-language skip qualification,
synthetic changed-state stop qualification, and focused persistence/orchestration/UI
tests pass. The complete build and 784-test suite plus all standard checks pass. The
corrected package passes its verifier and direct packaged smoke; mandatory Rescan
remains pending.

The mandatory Scan completed with 17,442 books and no Exact groups. Exact completed
NothingToDo. Candidate preparation found ten Unified groups, and cleanup performed
zero operations while safety-skipping all ten groups. State is authoritative at
revision 0 and `CandidateCleanupCompleted`. Preparing online metadata review then
terminated the packaged process before any mutation. Windows event 1026 reported
`Post-cleanup metadata review does not accept Candidate keeper selections.` WPF had
retained the reviewed Candidate rows and incorrectly forwarded those stale selections
after cleanup; the Application contract correctly requires the authoritative retained
snapshot alone. No pending mutation, staging/cache residue, cleaner process, or
Calibre process remained, so another Scan is not required. WPF now passes no Candidate
selections after cleanup and reports controlled argument/state failures in the UI
rather than allowing an unhandled process exception. The same-view-model transition
regression passes. The complete build and 784-test suite, formatting, diff,
diagnostics, and vulnerability checks pass. The rebuilt package SHA-256 is
`315db90798522dcf27c758b0da112ea2feda8b905db89d7e394df425e3890139`; its verifier
and direct packaged smoke pass. Preparing metadata review from the persisted
authoritative state then completed successfully for all 17,442 retained records. The
packaged process remained running. The revision 0 `CandidateCleanupCompleted` state
remained authoritative with no pending intent or deltas; metadata and transfer staging
were empty; and no Calibre or mutation worker process remained. There were zero
persisted apply overrides at this checkpoint. The current NeedsReview filter contains
15 unchecked Low-confidence rows. All 15 were reviewed and checked; the decision store
then contained 15 `Apply=true` overrides and no `Apply=false` overrides. The library
state remained authoritative at revision 0 with no intent, deltas, or uncertainty.
At 2026-08-20 21:18:22 +02:00, the caller explicitly confirmed a complete current
external backup for this metadata mutation run. Immediately before activation, only
the packaged app was running, state remained authoritative at revision 0 with no
pending intent or deltas, and metadata/transfer staging were empty. In-app activation
then stopped read-only with `The metadata review group is not current.` A retained
Candidate-row selection event attempted to retarget the post-cleanup review workspace,
which correctly contains singleton subjects only. No confirmation dialog, preflight,
intent, staging, worker, or mutation occurred; state remains authoritative at revision
0. WPF now retargets only when the current review workspace contains that Unified
group. The same-view-model stale-selection regression, complete build, and 784-test
suite pass; formatting, diff, and diagnostics are clean. The corrected package has
SHA-256 `cd7eb3436fdc2351fcb3a772daea7b933660c7dbed5099f5130d7b3cc58e69aa`;
its verifier and direct packaged smoke pass. The retry restored all 15 checked Low
rows, then metadata preflight stopped with `CANDIDATE.PLAN_INVALID: A singleton
metadata review subject is stale.` Updated and skipped counts remained zero. No intent,
staging, worker, or mutation occurred; state remains authoritative at revision 0.
The validator incorrectly treated retained books that still appear in historical,
safety-skipped Unified matching groups as ineligible singleton metadata subjects,
despite Candidate authority ending before the per-retained-record review. The obsolete
historical-group exclusion is removed while current workspace, target existence,
target uniqueness, and singleton membership checks remain. A two-book skipped-group
regression reaches metadata mutation for the retained singleton. The complete build
and 785-test suite, formatting, diff, diagnostics, and vulnerability checks pass. A
corrected package with SHA-256
`33b09c068658bbc27ad60074954d4637467352ce13fdad03e01584befd426d23`
passes its verifier and direct packaged smoke. The retry passed metadata plan
validation, then pre-mutation cover staging stopped after 1,001 of 2,015 covers with
`METADATA_COVER.HTTP_FAILED`. Updated and skipped counts remained zero. No intent,
delta, worker, Calibre process, staging residue, or mutation occurred; state remains
authoritative at revision 0. Bounded transient timeout, transport, HTTP, and refused
extra-redirect exhaustion now omit only the affected cover before intent creation and
continue staging; invalid trust/response/bounds and local staging failures still stop.
Omitted covers have a distinct result/UI count and privacy-safe aggregate reason logs.
The complete build and 787-test suite, formatting, diff, diagnostics, and vulnerability
checks pass. The corrected package with SHA-256
`c57b4b8fca589de45cd335397d6c72ac0cb9ca31c150f82d02e82a53cad3c2ab`
passes its verifier and direct packaged smoke. The next retry began preparing all
2,015 covers again because the previous failed batch was disposable and had been
cleaned. It was deliberately stopped read-only at 77 staged files after the user
requested smaller resumable batches; state remained authoritative at revision 0 with
no intent or deltas. Successful validated covers are now committed immediately to a
bounded `metadata-cover-cache/1.0` cache keyed by source ID, URL, and validation bounds.
Each hit revalidates size, SHA-256, JPEG dimensions, and physical path before copying
to disposable worker staging. Corruption/loss/unavailability becomes a miss, capacity
is bounded by bytes and entries, and progress reports cache hits/downloads/omissions.
Restart, corruption, unavailable-cache, capacity, trust, and transient-failure tests
pass. The complete build and 791-test suite, formatting, diff, diagnostics, and
vulnerability checks pass. The corrected package with SHA-256
`169af8e908748cdfa500631b0490148450e75aaed92874fb3b655e25c5ce9e85`
passes its verifier and direct packaged smoke. The next run persisted 1,997 validated
covers in the new cache and omitted five covers after bounded transient failures
(four HTTP, one timeout). Metadata mutation then committed 66 chunks and 6,600 state
deltas before chunk 67 failed with `metadata_authors_readback_failed`. The result
reported 6,599 updated fields, one verified-unchanged skip, and five omitted covers.
State is uncertain at revision 6,600 with the intent pending; no worker, Calibre
process, or temporary staging residue remains. The 1,997 cached covers remain reusable.
Explicit Rescan is mandatory before another mutation.

Deterministic reconstruction from the immutable revision-0 checkpoint and proposal/
cover caches identified operation 6,601, the first operation in failed chunk 67, as
Authors for record 3,183 with proposed value `Arnon Grunberg`. Baseline and fresh live
Calibre both contain exactly `Arnon Grunberg`, with per-author and book sort
`Grunberg, Arnon`; no pipe occurred in the proposal. This corrected the provisional
pipe-adapter diagnosis and identified stale immediate author projection as the failed
postcondition, analogous to the earlier cover projection. The worker now clears the
book cache after every `set_field` before read-back and retains whitespace-only author
comparison. A temporary synthetic Calibre library qualified the reconstructed shape:
set author, commit a title/path update, then repeat the same author in a later chunk;
fresh read-back succeeded with `Arnon Grunberg` and `Grunberg, Arnon`. All temporary
diagnostics were removed. Future chunk failures also log/display only the first failed
chunk offset, field/kind, and uncommitted successful-prefix count, without values.

Separate analysis found the pipe characters in existing Calibre author names, not
provider candidate authors. The revision-0 snapshot has 9,482 occurrences across
3,572 distinct pipe-form names; 3,557 have exactly one nonempty `Family| Given`
separator and 15 are ambiguous. Installed Calibre changes `|` to `,` but does not
reorder names. A safe policy now transforms only the executable subset to display
`Given Family` and exact sort `Family, Given`; direct tests cover particles and reject
multi-pipe/empty-side values. Its separate worker/UI execution slice remains pending
until metadata enrichment succeeds. The complete build and 798-test suite, formatting,
diff, diagnostics, and vulnerability checks pass. The corrected package with SHA-256
`ea289bbf1704e33cc6acffe857ff364469c6f30cd208d4de2f6160882eac448a`
passes its verifier and direct packaged smoke; Rescan is still mandatory.

The mandatory packaged Scan then completed authoritatively with 17,442 books, 18,712
present formats, and one Exact binary group after hashing 846 fresh file identities.
The group is two 433,243-byte EPUBs with SHA-256
`bebf2a1de8cb2d91dd7ad109be1f09d409d2e84ff5bc72ef47818f18b6ce63b1`, records
2,025 and 27,352. Both have title `Tuurrrlijk!`, the same ISBN and sole EPUB; record
2,025 additionally has a Google identifier and a non-truncated managed author path.
The raw scanned author remains `Costello| Jane pseud. van Catherine Isaac` in both
records, proving author normalization did not create the group. Existing hash-cache
history contains the fingerprint under prior identities, while both current paths were
freshly verified after Calibre metadata/path updates. Retain record 2,025 in normal
Exact review. A new complete current external backup confirmation is required before
Exact cleanup because the earlier backup predates the 6,600 committed metadata fields.
At 2026-08-21 16:31:31 +02:00, the caller explicitly confirmed that complete current
external backup. Immediately before activation, state was authoritative revision 0 at
`ExactReady`, with one group, no pending intent/uncertainty, and no Calibre process.
Exact cleanup then completed two operations in 2,739 ms: one EPUB was removed and its
empty record 27,352 was removed. Record 2,025 remains with its EPUB. State is
authoritative revision 2 at `CandidatePreparationReady`, with no pending intent,
uncertainty, worker, or staging residue.

Candidate preparation then completed authoritatively for 17,441 retained records.
Post-Exact refresh reused 18,711 fingerprints and 17,297 assessments, with two fresh
assessments. Candidate EPUB resolution used 1,417 cache hits and 155 inspections.
Residual analysis completed in 69,849 ms with nine Exact Metadata, 17 Expanded, and
22 Unified groups. State is authoritative revision 0 at `CandidateAnalysisReady`.
The caller completed review of all 22 groups; state remains mutation-free. Candidate
cleanup requires a new complete current backup because Exact cleanup changed the
library after the prior backup. At 2026-08-21 16:40:27 +02:00, the caller explicitly
confirmed that complete current external backup. Immediately before activation, state
was authoritative revision 0 at `CandidateAnalysisReady`, with no pending intent or
uncertainty, empty staging, and no Calibre process. Candidate cleanup remains pending
activation. Candidate cleanup then completed all 26 operations in 12,748 ms: one
format transferred, 13 formats removed, 12 records removed, and ten groups safely
skipped. State is authoritative revision 26 at `CandidateCleanupCompleted`, with no
pending intent or uncertainty. Live Calibre reports 17,429 books and 18,760 formats;
no worker or staging residue remains. Online metadata review preparation then
completed successfully for all 17,429 retained records. State remains authoritative
revision 26 at `CandidateCleanupCompleted`, with zero persisted Apply overrides in the
new generation, 1,997 reusable cached covers, and empty temporary staging. Low-
confidence review contained 11 unchecked rows. The caller reviewed and checked all
11; the decision store contains exactly 11 `Apply=true` overrides and no
`Apply=false` overrides. State remains mutation-free. Metadata apply requires a new
complete current backup because Candidate cleanup changed the library after the prior
backup. At 2026-08-21 16:57:15 +02:00, the caller explicitly confirmed that complete
current external backup. Immediately before activation, state was authoritative
revision 26 at `CandidateCleanupCompleted`, all 11 Low overrides remained selected,
1,997 covers were cached, temporary staging was empty, and no Calibre process was
running. Metadata apply reused/populated all required covers with zero omissions, then
committed 77 chunks and 7,700 metadata deltas before chunk 78 operation 38 failed with
`metadata_authors_readback_failed`. The result reported 7,698 updated fields, two
verified-unchanged skips, and zero omitted covers. State is uncertain at revision
7,726 (26 Candidate plus 7,700 metadata deltas) with the intent pending; no worker or
staging residue remains. The cache retains 2,006 covers. Explicit Rescan is mandatory.

Deterministic reconstruction from the immutable revision-26 checkpoint identified
global operation 7,738 as Authors for record 33,623. Baseline held two authors,
`Pohl| Frederik` and `Williamson| Jack`; Open Library proposed one combined value,
`Pohl, Frederik & Williamson, Jack`. Calibre stored one author as
`Pohl, Frederik ; Williamson, Jack`, so the worker correctly rejected the structural
mismatch. Metadata planning now splits only strict combined values whose every
` & ` segment is exactly `Family, Given`, producing separate display authors
`Frederik Pohl` and `Jack Williamson`. Ambiguous combined values omit only Authors,
continue other metadata, and report a distinct omitted-author count/warning. Installed
Calibre 9.12.0 qualified the exact replacement through the fixed worker, verifying two
authors and joined sort `Pohl, Frederik & Williamson, Jack`. All temporary diagnostics
were removed. The complete build and 805-test suite, formatting, diff, diagnostics,
and vulnerability checks pass. A corrected package rebuild remains pending; Rescan is
mandatory before another mutation.

The user requested eliminating manual transcription of UI results into acceptance
chat. Main-window status, errors, recovery actions, operation results, Exact summary,
Candidate summary, and metadata-review summary now log the exact displayed text.
Exact cleanup status, progress, and result do the same. Stable event names are
`UserVisibleStatus`, `UserVisibleError`, `UserVisibleRecoveryAction`,
`UserVisibleOperationResult`, `UserVisibleExactSummary`,
`UserVisibleCandidateSummary`, `UserVisibleMetadataReviewSummary`,
`UserVisibleExactStatus`, `UserVisibleExactProgress`, and `UserVisibleExactResult`.
They use the existing bounded logs under `%LOCALAPPDATA%\CalibreLibraryCleaner\logs`.
Focused tests assert verbatim text/event names; the complete build and 805-test suite,
formatting, diff, diagnostics, and vulnerability checks pass. The corrected package
with SHA-256 `f5e81eb5cc3ab5487190fe90dddd0bb523fc7e66ba03d07b173e63d95a0e4bd7`
passes its verifier and direct packaged smoke; Rescan remains mandatory.

The automatic-logging package then completed the mandatory Scan. Verbatim
`UserVisibleExactSummary` and `UserVisibleStatus` events reported no Exact file
duplicate groups and `Exact-only analysis complete: 17429 books, 0 exact file
duplicate groups, 0 missing format files.` State is authoritative revision 0 at
`ExactReady` in generation `23c9f6eb-ae2b-4ec5-bd64-a98267cd594a`, with no pending
intent or uncertainty. No manual UI transcription was required. Exact NothingToDo is
pending to advance the workflow. At 2026-08-21 18:45:56 +02:00, the caller explicitly
confirmed a complete current external backup containing the latest partial metadata
updates. Immediately before activation, state remained authoritative `ExactReady`
revision 0, temporary staging was empty, and no Calibre process was running.

Automatic UI logs then reported `Removed 0 duplicate format(s), merged 0 record(s),
and deleted 0 empty record(s).` Exact completed NothingToDo and advanced state
authoritatively to revision 0 at `CandidatePreparationReady`, with no pending intent
or uncertainty. The caller stated that complete current backups always exist and asked
not to repeat backup questions in chat; acceptance will rely on the application's
mandatory per-run confirmation prompt instead.

Candidate preparation then completed successfully. Automatic
`UserVisibleCandidateSummary` and `UserVisibleStatus` events reported 11 disjoint
Unified groups across 17,429 books. State is authoritative revision 0 at
`CandidateAnalysisReady` in generation `0a4fdd2f-4442-4cb9-b886-76e984b6a7a1`, with
no pending intent or uncertainty. The caller completed review of all 11 groups; state
remains revision 0 and mutation-free with empty staging. Candidate cleanup is pending
through the application's backup confirmation prompt. Automatic logs then reported
`Transferred 0 format(s), removed 1 format(s), and removed 1 record(s). Skipped 10
group(s).` The technical completion recorded two operations in 8,918 ms. State is
authoritative revision 2 at `CandidateCleanupCompleted`, with no pending intent,
uncertainty, worker, or staging residue. Live Calibre reports 17,428 books and 18,759
formats. Online metadata review preparation is pending.

Automatic `UserVisibleMetadataReviewSummary` and `UserVisibleStatus` events then
reported metadata review ready for 17,428 retained records: 3,121 Medium, 11 Low, and
14,296 Unavailable. The decision store contains 11 `Apply=true` overrides bound to
current generation `0a4fdd2f-4442-4cb9-b886-76e984b6a7a1`, revision 2, with no
`Apply=false` overrides. State remains authoritative and mutation-free. Metadata apply
is pending through the application's backup confirmation prompt.

Metadata apply then completed successfully. Automatic logs reported `Updated 19,114
metadata field(s); skipped 1 verified-unchanged field(s); omitted 0 transiently
unavailable cover(s); omitted 1 ambiguous author field(s).` The worker completed
19,115 operations in 2,876,736 ms; the one skip was proven unchanged after an author
read-back rejection. State is authoritative revision 19,117 at `Completed`, with no
pending intent or uncertainty. Live Calibre remains at 17,428 books and 18,759 formats;
temporary staging is empty and 2,006 covers remain cached.

Live post-metadata analysis found no remaining pipe characters because Calibre adapted
legacy pipe names to comma form. There are 3,349 conservative author entities whose
display name equals their sort value and contains exactly one nonempty comma; they
affect 8,718 book links and remain candidates for the separate approved author display/
sort normalization action.

The final action is now implemented in the existing fixed persistent worker path.
Author normalization is available only from authoritative `Completed` state and plans
one operation per book only when every linked author display equals its current exact
one-comma per-author sort. The typed request carries ordered display names and exact
sorts. Protocol `calibre-mutation-worker-protocol/1.2` verifies current names/sorts,
writes display authors, calls Calibre `set_sort_for_authors`, then verifies ordered
display names, every per-author sort, joined book sort, and managed path. Mixed or
ambiguous books remain unchanged. The same lease, intent, chunk, delta, checkpoint,
uncertainty, and in-app backup prompt rules apply. WPF exposes `Normalize author names`
and automatically logs progress/result. Installed Calibre 9.12.0 qualified
`Pohl, Frederik`/`Williamson, Jack` to `Frederik Pohl`/`Jack Williamson`, preserving
exact sorts and moving the managed path. All temporary qualification files were
removed. The complete build and 812-test suite, formatting, diff, diagnostics, and
vulnerability checks pass. The package with SHA-256
`13a4e0590c4664ef3cb9ab5cec09da76498ecf42c8c8c6ec421d9df04e5e3dc8`
passes its verifier and direct packaged smoke. Packaged final-action acceptance remains
pending.

The subsequent 1,869-book final action stopped at chunk 1 operation 2 after one
uncommitted success. Both operations targeted books sharing one Calibre author entity;
book-scoped cache invalidation was therefore insufficient between author mutations.
The fixed worker now globally clears shared author metadata around state capture,
author writes, and exact sort writes while retaining book-scoped clears for all other
metadata fields. A disposable Calibre 9.12.0 two-book shared-author qualification
passed the exact production-worker sequence and fresh-session display, sort, book-sort,
and path verification. Current library state is uncertain as required; Scan and a new
package remain mandatory before replay.

The first replay with global author-cache invalidation advanced past the original
shared-author pair but stopped at operation 32 of 1,867. The failed record reached its
intended state; the remaining fault came from calling
`set_sort_for_authors(update_books=True)` when the fresh target author already had the
exact requested sort, unnecessarily reprocessing paths for the full linked-book set.
The worker now performs that global update only on an actual per-author sort mismatch.
A disposable clone of the complete current catalog, with placeholder managed files,
then ran all 1,840 currently eligible operations through the exact production worker:
1,840 succeeded, no chunk failed, and fresh-session verification found zero remaining
eligible books and zero invalid paths. The clone was removed and all 814 tests pass.

The packaged replay subsequently completed all 1,835 planned operations and remained
authoritative. Fresh live verification found five newly exposed edge records after
shared-author convergence. The follow-up policy canonicalizes one-comma sort whitespace
and NFC for precondition identity while preserving strict family/given matching. An
exact disposable clone qualification completed those five records 5 of 5 with canonical
display names, exact sorts, and managed paths, leaving zero candidates. The complete
suite now passes 816 tests; only a short Completed-state packaged follow-up remains.

The packaged follow-up completed all five operations in 11,138 ms. State is
authoritative at revision 1,840 with no pending intent or uncertainty. A fresh Calibre
session reports 17,427 books, 18,758 formats, zero eligible comma-form books, zero
pipe-form books, and zero invalid managed paths. Worker and staging residue are empty.
Packaged restart loaded authoritative revision 1,840. Calibre opened the exact cleaned
library and remained responsive. After both GUIs closed, no matching process remained,
`metadata.db` accepted exclusive read access, and final verification found 17,427
books, 18,758 physical formats, zero missing formats, zero remaining author candidates,
zero pipe-form authors, zero invalid paths, and zero temporary or staging residue.

## Completed destructive packaged acceptance

The caller supplied the disposable library and confirmed a complete external backup
through the application's mandatory per-run prompts before mutation.

After confirmation, record actual start/end timestamps and phase timings for:

1. packaged executable launch;
2. Exact analysis and reviewed Exact cleanup;
3. Candidate preparation and Unified review;
4. offline fake/cached online proposal review;
5. checked metadata plus Candidate cleanup;
6. packaged restart and persisted-state load;
7. Calibre opening the cleaned result; and
8. final catalog/file/process/lock/temp/credential/mutation-residue checks.

Record deviations and failures exactly. Stop on failed or ambiguous mutation and
require Rescan. Do not infer a successful prefix. Do not start filename/folder
normalization or any step 9 behavior.