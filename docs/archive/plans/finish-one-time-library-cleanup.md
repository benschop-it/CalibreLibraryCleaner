# Finish the One-Time Library Cleanup Workflow

## Objective

Finish Calibre Library Cleaner as a focused Windows tool for one job:

1. analyze a messy disposable copy of a Calibre library;
2. remove duplicates and consolidate same-work records;
3. improve retained-book metadata from trusted online sources with minimal review;
4. apply approved cleanup through supported Calibre APIs;
5. normalize Calibre-managed names as the final improvement; and
6. let the user replace the original messy library with the cleaned copy.

This plan is the standalone authority for remaining work. When it conflicts with the
broader roadmap or older ADR priorities, the user's one-time cleanup goal above wins.
Do not add work outside this plan without explicit user confirmation after explaining
in plain English what concrete workflow problem it solves.

## Scope

- Preserve the staged Exact and Unified Candidate cleanup already proven on a large
  disposable library.
- Resolve online metadata for every record expected to remain after consolidation,
  including singleton books and one proposal per duplicate group.
- Combine Open Library and Google Books; tolerate either provider being unavailable.
- Add a UI setting for an optional Google Books API key, stored per user with Windows
  protection and never logged.
- Produce one coherent best-edition proposal containing title, authors, ISBN and
  other supported identifiers, publisher, publication date, language, series/index,
  and cover.
- Preserve local tags, ratings, comments, custom columns, and identifiers not supplied
  by the selected online edition.
- Show a visually distinct proposed-metadata row with source attribution, confidence,
  and an Apply checkbox.
- Select High- and Medium-confidence proposals by default; leave Low-confidence
  proposals unchecked and make them easy to filter/review.
- Apply only checked proposals to the final retained record through the fixed Calibre
  worker and supported Calibre APIs.
- Make Metadata and Expanded tabs unambiguously read-only evidence; Unified review is
  the only duplicate-cleanup selection surface.
- Prevent analysis and cleanup operations from overlapping.
- Create and validate a usable Windows release package including the PDF worker.
- Treat Calibre-managed filename/folder normalization as the last, separate slice.

## Out of scope

- More duplicate-matching calibration, heuristic tuning, embeddings, Ollama, local
  models, or new matching providers unless metadata-enrichment evidence later proves
  a specific blocker and the user approves it.
- Periodic hash verification, generalized cache architecture, incremental candidate
  neighborhoods, background maintenance, or synchronization features.
- Rollback, recovery, undo, resume, backup creation, or a second mutation engine.
- Cloud AI, LLM-generated metadata, OCR, book-content upload, or automatic prose
  generation.
- Cross-platform UI, plugins, multi-library comparison, or installer work beyond a
  practical Windows release ZIP unless requested.
- Automatically replacing local-only metadata fields.
- Claiming online metadata is infallible; confidence and provenance remain visible.

## Relevant requirements

- The working baseline is commit `8849631`; current baseline is `fd336c1`.
- From `8849631` to `fd336c1`, useful product changes were safer contradiction-aware
  duplicate matching, better title-variant candidate recall, Open Library same-work
  confirmation, and unchanged-file SHA-256 reuse.
- At the start of this plan Open Library confirmed same-work identity only. Rich
  edition proposals now use separate contracts/cache and remain distinct from work
  evidence and mutation authority.
- Ollama is observational, disabled unless configured, failed threshold calibration,
  and has no effect on grouping or cleanup. Do not expand it for this plan.
- Exact and Candidate mutation continue to require explicit external-backup
  confirmation, use only the persistent `calibre-debug` worker, stop on ambiguity,
  and never write directly to `metadata.db` or Calibre-managed files.
- Online lookups occur only after clear disclosure of transmitted field categories.
- Provider failure must not block duplicate cleanup; it leaves metadata unchanged.

Useful context:

- `docs/functional-requirements.md`
- `docs/safety-and-rollback.md`
- `docs/workflows/staged-large-library-acceptance.md`
- `docs/archive/plans/open-library-bibliographic-evidence.md`
- `docs/archive/plans/local-ollama-embedding-evaluation.md`

## Existing implementation inspected

- Exact Scan, Exact review/cleanup, residual reconciliation, Candidate preparation,
  Unified keeper/Skip review, and Candidate cleanup are implemented.
- A destructive disposable-library run completed against 27,952 records.
- Candidate cleanup transfers formats and removes source formats/records without
  invoking online metadata. Explicit post-cleanup review and metadata-only mutation
  operate only on retained records.
- Existing recommendations can choose a whole-record metadata source but do not
  provide online edition metadata or field-level proposal/application.
- Open Library same-work search remains unchanged. A separate cache-first rich-edition
  boundary parses one nested relevance-ranked edition per work and is invoked only by
  an explicit action after Candidate cleanup.
- Google Books metadata lookup and protected credential UI/storage now exist through
  the provider-neutral boundary. Deterministic provider fusion and group/singleton
  subject construction now exist. Explicit post-cleanup preparation, scalable
  proposal review, durable Apply overrides, bounded cover staging, and verified
  metadata mutation now exist. Final name normalization does not exist.
- Metadata and Expanded evidence tabs are read-only; Unified review remains the only
  duplicate cleanup selection surface.
- WPF library selection, Scan, Load, Exact cleanup, Candidate preparation/cleanup,
  online settings, and review writes share one operation coordinator.
- Release publish does not reliably include the required PDF worker.

## Proposed design

### Final workflow

```text
Scan -> Exact review/cleanup -> Candidate preparation -> Unified review/cleanup
  -> explicit online metadata review for retained records
  -> checked metadata-only update -> Calibre name normalization -> done
```

After Candidate cleanup, explicit metadata preparation creates one review subject for
every retained record. Candidate preparation, Unified review, restart/load, and state
projection do not invoke edition providers.

### Provider model

Add a provider-neutral rich edition result separate from existing same-work evidence.
Each result carries provider, work/edition IDs, retrieval time, query fields,
available bibliographic fields, cover reference, and complete provider/policy version.

Use Open Library and Google Books independently, then choose one coherent edition:

1. exact validated ISBN agreement;
2. title/author/language compatibility;
3. edition/publisher/date compatibility;
4. agreement between providers; and
5. field completeness and cover availability.

Do not construct a synthetic edition by freely mixing conflicting provider records.
The selected edition may fill a missing field from another provider only when both
records have the same validated ISBN or otherwise proven identical edition identity.
Provider disagreement lowers confidence.

Confidence bands:

- **High**: strong edition identity, selected by default;
- **Medium**: convincing best edition with no decisive conflict, selected by default;
- **Low**: ambiguous/conflicting match, unchecked and requires review;
- **Unavailable**: no proposal and no metadata mutation.

### Review UI

Use one scalable metadata-review surface. For each group/singleton, show compact
current retained metadata and one visually distinct proposed row containing:

- Apply checkbox;
- confidence and short reasons;
- provider/source identity;
- title, authors, ISBN/identifiers, publisher, date, language, series/index; and
- current/proposed cover indication and preview where practical.

Default the view to `Needs review`, meaning unchecked Low-confidence proposals and
provider conflicts. Offer simple filters for All, Applied automatically, Needs review,
and Unavailable. High/Medium rows remain deselectable. Do not require opening every
proposal.

### Google Books key

Add an Online metadata settings dialog with a masked API-key field and Save/Replace/
Clear actions. Store the key per Windows user using OS-protected storage outside the
repository/library. Never log, export, cache, or display the plaintext key. Google
Books remains disabled without a key; Open Library continues independently.

### Metadata mutation

Extend the existing typed worker protocol narrowly with one metadata-update operation.
The operation carries the expected target record, selected edition identity, and only
approved supported fields. Validate bounds and exact protocol shape. Apply metadata
through Calibre's supported database API after Candidate source records are deleted,
then verify
read-back. Any failed or ambiguous update stops the mutation run under the existing
Rescan rule.

Preserve local tags, ratings, comments, custom columns, and unrelated identifiers.
Replace the supported bibliographic fields and cover only when the proposal is
checked. Unchecked/Unavailable proposals leave metadata unchanged.

### Filename and folder normalization

Implement only after metadata enrichment is accepted. Use a supported Calibre API or
command that lets Calibre rename its managed files/directories from the final metadata.
Never rename paths directly. Confirm behavior on a disposable library before enabling
it in production. Keep it an explicit final action because it affects many paths but
adds no matching or metadata value.

The user explicitly approved correcting existing `Family| Given` author metadata.
The revision-0 acceptance snapshot contains 9,482 occurrences across 3,572 distinct
pipe-form author names. Exactly 3,557 distinct names have one nonempty separator and
are safely transformable; 15 distinct multi-separator/empty-side/otherwise ambiguous
names remain unchanged for review. Preview the executable and ambiguous counts. Write
display names as `Given Family`, preserve the explicit family side in exact per-author
sort `Family, Given`, let Calibre update book-level sorts and managed paths, and verify
all four outputs through the fixed worker. This remains a separate backup-confirmed
action after metadata enrichment succeeds. The complete build and 798-test suite,
formatting, diff, diagnostics, and vulnerability checks pass. The corrected package
with SHA-256 `ea289bbf1704e33cc6acffe857ff364469c6f30cd208d4de2f6160882eac448a`
passes its verifier and direct packaged smoke; Rescan is still mandatory.

## Files expected to change

- Domain values/policies for rich edition candidates, proposal confidence, and
  provider fusion
- Application provider/cache/settings ports, enrichment orchestration, proposal
  generation, cleanup contracts, and operation coordination
- Infrastructure Open Library enrichment, Google Books client/cache, protected key
  storage, cover retrieval, Calibre worker protocol, and release publishing
- WPF settings dialog, metadata-review rows/filters, read-only evidence tabs, and
  shared operation state
- focused Domain/Application/Infrastructure/WPF tests
- README and current authoritative product/workflow/safety/test documentation
- this plan and `docs/plans/README.md`

Do not modify Ollama/local-embedding code except to remove misleading UI/docs if
necessary. Do not add generalized cache or recovery infrastructure.

## Safety considerations

- Lookup sends only disclosed bibliographic fields; never paths or book content.
- API keys and provider payloads never enter logs.
- Cover downloads are bounded by HTTPS endpoint, type, dimensions/bytes, timeout, and
  no redirects to untrusted schemes.
- Online results are proposals, never direct mutation authority.
- Only checked proposals become typed worker operations.
- The worker verifies target identity and metadata read-back and stops on ambiguity.
- All Calibre-managed path changes use supported Calibre behavior.
- Scan, Load, Exact cleanup, Candidate preparation, Candidate cleanup, and final
  normalization cannot overlap.

## Implementation steps

1. **Reset product documentation.** Replace the broad future roadmap with this
   one-time finish line and correct stale Open Library/Ollama/hash statements.
2. **Remove review ambiguity and operation races.** Make Metadata/Expanded evidence
   read-only, clarify Prepare versus Cleanup labels, and enforce one shared operation
   gate.
3. **Add rich provider contracts and Open Library edition metadata.** Produce cached
   proposals without mutation; validate against a small labeled bibliographic set.
4. **Add Google Books and protected API-key settings.** Keep provider failures
   independent and disclose transmitted fields before lookup.
5. **Fuse providers into one coherent proposal per group/singleton.** Implement
   confidence/default-selection policy and preserve local-only fields.
6. **Build scalable metadata review.** Add proposed rows, filters, cover availability, and
   checkbox overrides; verify thousands of rows remain responsive.
7. **Apply approved metadata.** Extend the fixed worker, integrate metadata operations
   into final cleanup, and verify supported fields/covers through Calibre read-back.
8. **Package and accept the release.** Include PDF worker, document prerequisites,
   run the complete packaged workflow on a disposable representative library, and fix
   only observed workflow defects.
9. **Normalize managed names last.** After explicit confirmation, add one supported
   final action and repeat disposable-library acceptance.

Implement and verify one numbered step at a time. Do not begin the next step merely
because it appears in this plan if the current step exposes a product decision; ask
the user.

## Tests

Keep tests proportional to this workflow:

- provider parsing, bounds, provenance, cache identity, and independent failure;
- ISBN/title/author/edition matching, cross-provider agreement/conflict, confidence,
  and deterministic coherent-edition selection;
- High/Medium checked defaults, Low unchecked default, user override persistence,
  singleton and Unified-group coverage;
- protected key Save/Replace/Clear and absence from logs/cache/export;
- metadata update protocol validation, supported-field replacement, local-field
  preservation, cover handling, read-back, and stop-on-failure;
- operation commands cannot overlap;
- Metadata/Expanded choices cannot imply mutation authority;
- release package contains WPF app, PDF worker, and required runtime assets;
- one final disposable-library end-to-end acceptance using the packaged build.

Do not create tests for periodic maintenance, recovery, synchronization, plugins,
cross-platform behavior, Ollama activation, or other non-goals.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Provider integration tests use fake HTTP. Live-provider and destructive Calibre tests
remain explicit opt-in runs against temporary/disposable data. Final release acceptance
uses no personal production library.

## Installed-Calibre metadata API qualification

The mandatory spike ran on 2026-08-16 against installed Calibre 9.12.0 and a
caller-created, explicitly confirmed disposable library. The isolated qualification
script is `scripts/calibre_metadata_api_spike.py`; it creates only synthetic records,
prints no bibliographic values or paths, and requires a destructive confirmation token.

Observed `Cache` API contract:

- `set_field(name, {book_id: value}, do_path_update=True)` returned the changed ID
  and exactly round-tripped title, authors, author sort, identifiers, publisher,
  publication date, languages, series, and series index.
- `set_cover({book_id: bytes})` exactly round-tripped a Calibre-generated JPEG.
- Title and authors each changed the catalog path and physically moved the managed
  directory; the cover followed the move and the old path was retired. Author sort,
  identifiers, publisher, publication date, languages, series, and series index did
  not change the path.
- Identifier writes replace the complete identifier map. Production mutation must
  read the current map and submit a merged map so unrelated identifiers survive.
- Tags, rating, comments, and all six writable custom columns in the disposable
  library survived the qualified field updates. Its seventh custom column was a
  computed composite and was not writable.
- One initialized `Cache` session exposed updated metadata before and after a later
  `remove_books({source_id}, permanent=False)` call. The source record and managed
  directory disappeared, while the keeper and its updated metadata remained visible.
- Non-permanent record removal created a Calibre trash entry, as expected. The spike
  deleted only its synthetic entry by ID through `delete_trash_entry(book_id, "b")`.

Post-spike cleanup matched the read-only baseline: 27,952 records, 74,834 files,
zero synthetic catalog/trash/path residue, and no remaining Calibre process.

Consequence for the remaining plan: approved title or author mutation already performs
Calibre-managed path normalization for that record. Production metadata mutation must
therefore treat those path moves as part of step 7, and step 9 must not assume all path
normalization is deferred until the final operation.

### Step 7 execution checklist

- [x] Accept only checked review subjects whose library root, generation, revision,
  subject identity, edition identity, and current keeper target remain compatible with
  the cleanup request. Reject stale or retargeted inputs before mutation.
- [x] Download approved covers before the durable mutation marker through a bounded
  Infrastructure HTTPS client. Validate endpoint policy, response type, bytes, and
  image dimensions; do not let the worker perform network access.
- [x] Extend the fixed typed worker protocol with a bounded metadata-update payload.
  Keep metadata updates ordered before transfers, format removals, and record removals
  in the same initialized `Cache` session.
- [x] Read the live identifier map in the worker and merge approved identifiers by
  normalized type before calling `set_field`; never replace unrelated identifiers from
  a snapshot-derived map.
- [x] Snapshot tags, rating, comments, and stored custom-column values in the worker;
  verify they remain equal after mutation. Computed composite columns may recalculate
  from the approved bibliographic changes and are not writable local values.
- [x] Use `set_field(..., do_path_update=True)` for supported fields and `set_cover`
  for a downloaded cover. Treat title/author managed-path changes as expected and use
  the record ID plus live `Cache` reads for every postcondition.
- [x] Verify exact read-back for every submitted field, the merged identifier map, and
  cover bytes before acknowledging success. Any failed or ambiguous postcondition
  stops the chunk and requires Rescan; never continue to source removals.
- [x] Project successful metadata changes with existing `SetMetadataLibraryStateDelta`
  values without persisting or trusting a pre-mutation managed path.
- [x] Add focused contract, protocol, ordering, preservation, stale-input, read-back
  failure, and WPF request-wiring tests, then run the full serial verification suite.
- [x] Do not begin release packaging or final name normalization in this slice.

Production qualification on 2026-08-16 exercised the fixed worker implementation
against synthetic records in the confirmed disposable Calibre 9.12.0 library. All
supported fields and a staged cover verified; two-letter language input read back in
Calibre's canonical three-letter form; unrelated identifiers and local fields were
preserved; and source removal followed metadata in the same Cache session. Cleanup
returned the library to 27,952 records and 74,834 files with zero synthetic catalog,
trash, or path residue.

## Risks

- Online sources can be incomplete or wrong. Confidence, provenance, unchecked Low
  proposals, and user deselection mitigate this; never claim 100% correctness.
- A best-ranked edition may differ from the file's actual edition. The user explicitly
  accepted best-online-edition behavior; disagreement must lower confidence.
- Thousands of provider calls can hit quotas. Cache results, bound concurrency, use
  the Google key when configured, and allow Open Library-only continuation.
- Cover replacement can degrade a good local cover. Show provenance/preview and make
  the whole proposal deselectable; consider a later per-cover override only if real
  use proves necessary and the user approves it.
- Metadata updates can trigger Calibre path changes depending on API behavior. Test
  this before enabling separate final name normalization.

## Unresolved questions

No blocking product questions remain for steps 1-6.

Step 7 installed-Calibre API and production-worker qualification is complete. Title
and author updates move managed paths; the worker uses record IDs and returns live
paths instead of assuming pre-mutation paths.

Before name normalization (step 9), present the tested supported Calibre mechanism and
its observed effects for explicit user confirmation.

## Progress

- [x] One-time product goal and non-goals confirmed.
- [x] Baseline comparison `8849631` -> `fd336c1` reviewed.
- [x] Metadata review defaults and provider strategy confirmed.
- [x] Google Books API-key UI requirement confirmed.
- [x] Product documentation reset completed.
- [x] UI ambiguity and operation overlap removed.
- [x] Open Library rich metadata proposals implemented.
- [x] Google Books/key settings implemented.
- [x] Provider fusion and metadata-review subject construction implemented.
- [x] Scalable proposal review UI and override persistence implemented.
- [x] Approved metadata mutation implemented and qualified on Calibre 9.12.0.
- [x] Packaged release accepted on disposable library.
- [x] Final Calibre-managed name normalization approved and implemented.

Step 7 verification completed with 761 serial tests passing, formatting and diff
checks clean, no diagnostics, and no vulnerable direct or transitive packages.

### Step 8 execution checklist

- [x] Revalidate the complete steps 1-7 worktree before changing release behavior.
- [x] Measure framework-dependent and self-contained `win-x64` publish sizes and
  choose one deployment model for this one-time Windows workflow.
- [x] Publish WPF and PDF worker independently; never depend on the WPF
  `AfterTargets="Build"` copy target for release completeness.
- [x] Assemble a deterministic release directory containing the WPF executable,
  runtime dependencies, complete PDF worker executable/runtime assets, and the
  Infrastructure assembly with its fixed embedded Calibre mutation worker resource.
- [x] Exclude symbols, tests, caches, credentials, logs, persisted library state,
  cover staging, temporary files, and source-machine paths.
- [x] Generate one versioned ZIP plus a manifest of normalized relative file names,
  byte sizes, and lowercase SHA-256 hashes. Keep ZIP entry ordering and timestamps
  deterministic for identical inputs.
- [x] Add automated package-content, manifest/hash, exclusion, embedded-worker, and
  packaged-executable launch-smoke checks.
- [x] Document supported Windows, deployment runtime, Calibre 9.11 through 9.x,
  external-backup confirmation, and disposable-library-first operation.
- [x] Run the packaged executable directly for non-destructive smoke acceptance.
- [x] Before destructive packaged acceptance, obtain an explicit caller-selected
  disposable representative library and explicit confirmation of its complete
  external backup. Do not infer either from earlier API-spike approval.
- [x] With live provider requests disabled unless separately approved, execute and
  record the complete packaged Exact/Candidate/metadata/restart/Calibre-open workflow,
  actual timings, deviations, process/lock/temp state, and final technical baseline.
- [x] Do not begin final filename/folder normalization or any step 9 implementation
  before packaged cleanup and metadata acceptance completed.

The selected package is framework-dependent `win-x64`: measured merged publish size
was 23.18 MiB/79 files versus 230.36 MiB/505 files self-contained. The one-time tool
already requires installed Calibre, so requiring the .NET 10 Desktop Runtime avoids a
roughly tenfold package expansion. The PDF worker remains in `pdf-worker/` because
flat merging exposed a same-named native dependency collision.

Package `CalibreLibraryCleaner-0.1.0-win-x64-framework-dependent.zip` contains 79
manifested payload files, is 9,356,417 bytes compressed, and has SHA-256
`99a4216c96a503098645c22b9d1fa4653c0786de9da33c560140dbd74b34da61`. The package
verifier and direct packaged smoke pass. The deterministic builder previously produced
an identical hash across two complete builds; reproducibility has not been rerun after
the acceptance fixes. Destructive packaged metadata acceptance remains pending retry
under the caller's current external-backup confirmation.

The first packaged acceptance attempt exposed a workflow defect and was stopped. The
old package completed Exact analysis (27,952 books, 29,235 formats, 5,750 groups,
170,761 ms) and Exact cleanup (13,542 operations, 247,938 ms), then completed local
Candidate analysis for 21,190 records and 3,190 Unified groups in 129,882 ms. It began
Open Library enrichment before Unified cleanup; the user canceled before Candidate
mutation. The corrected package makes provider resolution an explicit action after
Candidate cleanup and invalidates persisted pre-fix workflow checkpoints with
`staged-cleanup/1.1.0`.

A later metadata-review rerun demonstrated partial cache reuse: at 11,834 of 17,463
Open Library queries, 11,081 were cache hits and 752 were online. The remaining cost
came from queries not completed by earlier canceled runs and transient unavailable
results that were not cached. The progress bar then jumped to 100% because the old
resolver counted request-cap-deferred misses as completed. The corrected resolver
writes every completed result immediately, caches unavailable results for a six-hour
cooldown, breaks at the true processed count when capped, and reports the uncached
deferred count. At cancellation, 12,319 proposal cache entries were retained.

The first complete post-fix query run cached all 17,748 current entries, then the
packaged process terminated with unhandled .NET exception `0xE0434352`. Windows event
1026 identified a WPF binding error: read-only detail `TextBox.Text` bindings defaulted
to TwoWay and attempted to write the getter-only `Reasons` property when the first
selected detail row was assigned; `Provenance` had the same latent defect. Both are now
explicit OneWay bindings with an architecture regression guard. The crash left the
library database unchanged, zero cache temp files, zero cover staging directories,
and zero child processes.

The corrected review rerun reconstructed 17,514 retained-record subjects from the
17,748-entry provider cache. All 663 expired transient failures were retried: 9 became
proposals, 2 ambiguous, 651 NotFound, and 1 remained unavailable. Fusion produced
3,141 Medium proposals selected by default, 21 unchecked Low proposals, and 14,352
Unavailable subjects. A second visual defect placed the subject list, splitter, and
details in a grid with only two declared rows, causing the details to cover the list.
The package now declares separate list/splitter/detail rows, preserves a 180-pixel
minimum list, and leaves detail growth unbounded above its 160-pixel minimum. The
relevant WPF/architecture suite passes 75 tests. Metadata mutation has not started.

The first metadata-only mutation attempt was authorized after all 21 Low proposals
were reviewed and selected, for 3,162 checked subjects total. Preflight stopped with
`CANDIDATE.METADATA_COVER_STAGING_FAILED` before tool discovery, mutation lease,
durable marker, worker start, or metadata update. Revision 7,813 remains authoritative
at `CandidateCleanupCompleted`, with no pending mutation, cover staging residue, cache
temporary file, or Calibre child process. The failure was caused by Open Library cover
URLs returning a two-redirect chain while the stager required an immediate HTTP 200.
The stager now follows only the observed identity-preserving HTTPS chain through one
of two numeric `archive.org` cover paths to
`ia<number>.us.archive.org/view_archive.php`, requires the terminal `file` query to
equal the original numeric cover ID, and rejects off-list, mismatched, or additional
redirects. All 10 focused cover tests pass.

The first redirect-capable package still stopped safely with the same generic staging
code and zero updates. A bounded anonymous validation of all 2,019 distinct cached
cover references found 2,017 immediate valid JPEGs, one transient Internet Archive
`502/303` outcome, and one timeout; both transient covers later returned valid JPEGs.
The stager now makes at most three attempts per cover with one- then two-second
backoff for timeout, transport, `408`, `429`, and selected `5xx` responses. Trust,
redirect-count, content-type, byte, dimension, and JPEG failures remain immediate.
Privacy-safe failure codes now distinguish timeout, HTTP, response validation, and
local staging failures. The next package stopped safely with `RESPONSE_INVALID`, zero
updates, and unchanged revision 7,813. This matched the observed intermittent extra
redirect: exceeding the strict two-hop limit now restarts from the original trusted
URL without following the extra destination, still bounded to three attempts. Failure
results also retain only aggregate completed/total cover counts for diagnosis. The
full suite, package verifier, and direct packaged smoke pass; metadata retry remains
pending.

The request-zero failure was then reproduced directly from the persisted authoritative
review workspace without invoking Calibre. Its public Open Library cover ID correctly
matched the initial URL, but Open Library mapped it to a different internal Internet
Archive member ID with leading zeroes. The archive path and terminal CDN `file` value
matched each other exactly, and the 50,347-byte JPEG passed the production dimension
parser. The stager now validates continuity from the trusted archive member filename
to the CDN parameter instead of requiring equality with the public cover ID, and it
accepts leading-zero numeric members only when their parsed value is positive. The
actual first checked cover now stages successfully through the current implementation.
The temporary diagnostic source and its solution/process residue were removed. The
full build, 776-test suite, package verifier, and direct packaged smoke pass; metadata
retry remains pending.

The next metadata attempt staged all 2,029 covers and started the worker with 20,206
metadata operations. Four complete 100-operation chunks committed, updating 400
fields, then chunk 5 failed. The state is correctly uncertain at revision 8,213 with
the mutation intent still pending; no worker or staging residue remains. Further
mutation is prohibited until explicit Rescan. Read-only comparison against the intact
revision-7,813 checkpoint proved the reconstructed operation order matches all 400
committed deltas and identified operation 412 as the first changed proposal not present
in the live catalog. It was a one-value language update whose normalized provider code
is not recognized by the platform ISO culture set; the preceding title, identifiers,
publisher, and publication-date updates for that subject persisted. This strongly
identifies the worker failure as Calibre rejecting an unsupported provider language.
The metadata planner now omits unsupported provider language values, preserving the
current local language. Worker chunk failures retain and display/log the bounded
technical worker code plus committed/total counts, and operation progress displays
exact completed/total counts. The full build and 778-test suite pass. A corrected
package verifier and direct packaged smoke also pass. Mandatory Rescan remains
pending.

After the mandatory Rescan, Exact NothingToDo, 77-group Candidate review, and
Candidate cleanup completed authoritatively. Candidate mutation used 171 operations:
19 formats transferred, 86 formats removed, 66 records removed, and 11 groups
skipped. Metadata review then covered 17,448 retained records with 3,134 Medium,
20 reviewed-and-selected Low, and 14,294 Unavailable subjects. The next 19,247-
operation metadata run committed 13 complete chunks (1,300 fields) before chunk 14
failed with `metadata_readback_failed`. State is uncertain at revision 1,471 with a
pending mutation intent; no worker or cover residue remains, so another explicit
Rescan is mandatory.

Read-only comparison against the intact revision-171 checkpoint exactly matched all
1,300 committed operation IDs. The last definite live change was operation 1,353
(Title); operation 1,354 (Authors) was the first changed field absent afterward. The
proposed and live author lists had equal counts/order/case/content after whitespace
collapse, proving Calibre canonicalized internal author whitespace while the worker
required raw string equality. The worker now verifies ordered authors after
whitespace canonicalization while still rejecting substantive changes. A synthetic
in-memory qualification executed by installed `calibre-debug` passed both the
whitespace-only and substantive-mismatch cases. Scan persistence now explicitly shows
`Saving authoritative scan state...` instead of leaving the premature completed
message visible. The full build and 779-test suite pass. The corrected package passes
its verifier and direct packaged smoke; mandatory Rescan remains pending.

The next mandatory Rescan completed authoritatively with 17,448 books, one Exact file
group, and zero missing formats. It also exposed a committed UI regression from
`4dcbd9d`: splitter-style rows had been inserted into the Exact tab while its member
panel remained assigned to row 2, collapsing selected members to a 5-pixel row. The
Exact tab now restores the established `Auto / * / *` group/member layout, with an XML
structure guard and live XAML activation coverage. The full 780-test suite, format,
diff, diagnostics, and vulnerability checks pass. The authoritative ExactReady scan
can be loaded without another Scan. The corrected package passes its verifier and
direct packaged smoke.

Packaged visual acceptance confirmed the restored selected-member panel is usable,
with nonzero measured bounds of 1,158 by 119 pixels. The authoritative one-group
ExactReady state remained unchanged; keeper/Skip review and cleanup remain pending.

Exact cleanup then completed two operations, removing one format and its empty record.
The next Candidate analysis contained 15 Unified groups; reviewed cleanup processed
five groups with 11 operations (one transfer, five format removals, five record
removals) and safely skipped ten groups with incomplete physical facts. Metadata
review covered 17,442 retained records: 3,128 Medium, 19 reviewed-and-selected Low,
and 14,295 Unavailable. The next metadata run committed 22 chunks (2,200 fields) of
19,207 operations before chunk 23 failed with `metadata_readback_failed`. State is
uncertain at revision 2,211 with a pending intent; no worker or staging residue
remains, so explicit Rescan is mandatory.

Read-only reconstruction from revision 11 matched all 2,200 committed operation IDs.
The last definite changed field was operation 2,234; operation 2,236 was a cover write,
and the next subject remained untouched. The managed `cover.jpg` was written during
the run and exactly matched the expected Calibre-normalized JPEG in a fresh session.
This identified stale immediate `get_metadata(...cover_as_data=True)` projection as
the failed postcondition. The worker now clears the affected book cache and verifies
through Calibre's dedicated `cache.cover(book_id)` API. A real caller-created temporary
Calibre library qualified the refreshed readback, and temporary diagnostics were
removed. Worker errors now distinguish cover, field, and final-context readback codes.
The full build and 780-test suite pass. The corrected package passes its verifier and
direct packaged smoke; mandatory Rescan remains pending.

After the user approved a narrower failure policy, metadata operations now snapshot
the target field/cover, managed path, author sort, and protected local fields before
write. A rejection continues only when the complete post-state is proven unchanged;
the worker returns typed `isSkipped` evidence and the application persists a no-op
skip delta so mutation-intent accounting remains exact. Changed or unreadable
post-state still stops as ambiguous with mandatory Rescan. Updated and skipped fields
are counted separately. The fixed protocol is `calibre-mutation-worker-protocol/1.1`.
Installed Calibre qualified an unsupported-language rejection as skipped unchanged,
while a synthetic changed-then-thrown operation still stopped. Focused protocol,
state replay, orchestration, and WPF reporting tests pass. The complete build and
784-test suite, formatting, diff, diagnostics, and vulnerability checks pass. The
corrected package passes its verifier and direct packaged smoke.

The mandatory Scan then completed with 17,442 books and no Exact groups. Exact was
NothingToDo. Candidate preparation found ten Unified groups, and Candidate cleanup
performed no operations while safety-skipping all ten groups. The resulting revision
0 state is authoritative at `CandidateCleanupCompleted`. Preparing online metadata
review then crashed read-only because WPF passed the still-visible Candidate keeper
selections into the post-cleanup review contract, which intentionally accepts only
the authoritative retained snapshot. There was no pending mutation, staging/cache
residue, cleaner process, or Calibre process, so another Scan is not required. WPF
now passes an empty keeper-selection list after cleanup and converts controlled
argument/state failures into actionable UI errors instead of unhandled process
exceptions. The same-view-model cleanup-to-review regression passes. The complete
build and 784-test suite, formatting, diff, diagnostics, and vulnerability checks
pass. The rebuilt package passes its verifier and direct packaged smoke; rerunning
Prepare online metadata review from the persisted state completed successfully for
all 17,442 retained records. The packaged process remained running, the authoritative
revision 0 `CandidateCleanupCompleted` checkpoint remained unchanged with no pending
intent or deltas, both staging directories remained empty, and no Calibre or mutation
worker process remained. The current NeedsReview filter contains 15 unchecked Low-
confidence rows, with zero persisted apply overrides before review. All 15 were then
reviewed and checked; the decision store contains exactly 15 `Apply=true` overrides
and no `Apply=false` overrides. The authoritative library state remains unchanged.
At 2026-08-20 21:18:22 +02:00, the caller explicitly confirmed a complete current
external backup for this metadata mutation run. Immediately before activation, only
the packaged app was running, the authoritative state remained revision 0 with no
pending intent or deltas, and both staging directories were empty. In-app activation
then stopped read-only with `The metadata review group is not current.` A retained
Candidate-row selection event attempted to retarget the post-cleanup review workspace,
which correctly contains singleton subjects only. No confirmation dialog, preflight,
intent, staging, worker, or mutation occurred; state remains authoritative at revision
0. WPF now retargets only when the current review workspace actually contains that
Unified group. The same-view-model stale-selection regression, complete build, and
784-test suite pass; formatting, diff, and diagnostics are clean. A corrected package
with SHA-256 `cd7eb3436fdc2351fcb3a772daea7b933660c7dbed5099f5130d7b3cc58e69aa`
passes its verifier and direct packaged smoke. The retry restored all 15 checked Low
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
provider candidate authors. Installed Calibre's author input adapter changes `|` to
`,` but does not reorder names; `Woods| Stuart` therefore becomes `Woods, Stuart`, not
the requested `Stuart Woods`. The safe author normalization policy described above is
implemented and directly tested, but its worker protocol/UI execution slice remains
pending until metadata enrichment succeeds. The corrected package above contains the
fresh author read-back fix; Rescan is still mandatory.

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

The first packaged final action completed authoritatively at revision 19,151 but
planned only 34 book records. Read-only comparison then found 8,735 pipe-form author
occurrences in projected state while live Calibre had already adapted every pipe to a
comma. Live Calibre still contained 3,319 safe comma-form author entities. Policy
`author-name-normalization/1.1.0` therefore accepts either an exact comma display/sort
pair or a projected one-pipe display whose computed comma sort exactly matches the
stored sort. Both forms emit the same comma-form live precondition, display-form write,
and exact sort list; whole-book ambiguity still excludes the record. Replay and final
catalog acceptance remain pending.

The required post-failure Scan published 127 new Unified Candidate groups after the
author display changes. Recovery must not execute these newly inferred consolidations
without review. The Unified Candidate tab now provides a phase-gated `Skip all groups`
command that changes review selections only and performs no mutation. The complete
812-test suite, package verifier, and direct smoke pass for this recovery build.

Recovery Candidate cleanup then skipped all 127 groups with zero format or record
changes and advanced authoritatively to `CandidateCleanupCompleted`. Metadata review
resolved 17,378 provider queries (11,897 cached and 5,481 online) and produced 3,755
default-checked Medium proposals, 97 Low, and 13,575 Unavailable. Reapplying completed
metadata work is out of scope, so the review now provides `Clear all Apply`: it creates
generation-bound false overrides for all applicable proposals in one bounded decision
write. An all-unchecked apply advances directly to `Completed` before tool discovery,
worker startup, or mutation intent. The complete 814-test suite passes.

The next 1,869-book normalization attempt failed in chunk 1 operation 2 after one
uncommitted success. The first two planned records, 32,987 and 32,988, shared Calibre
author entity 10,618. Fresh read-only state remained internally consistent, proving
the earlier long-run-cache theory wrong: book-scoped invalidation left shared author
metadata stale between adjacent operations. Author operations now clear Calibre's
global metadata cache before state capture, after `set_field`, and after
`set_sort_for_authors`; unrelated fields retain book-scoped invalidation. A generated
disposable Calibre 9.12.0 library with two books sharing one comma-form author passed
both operations in one production-worker chunk. A fresh session verified both display
names, per-author sorts, joined book sorts, and managed paths, and all temporary data
was removed. The complete 814-test suite, formatting, diagnostics, diff check, and
vulnerability audit pass. A new packaged replay remains pending after the mandatory
Scan clears the current uncertainty.

The next replay advanced past the original collision but failed at operation 32 of
1,867 after 31 uncommitted successes. All records in that neighborhood shared the
same author, and the failed target itself reached the intended display, sort, and
path. The remaining trigger was unconditional `set_sort_for_authors(update_books=True)`:
even when the fresh target author entity already had the exact requested sort, Calibre
reprocessed paths for its entire growing linked-book set. The worker now calls that
global API only when fresh per-author sorts differ; exact-sort cases proceed directly
to complete verification.

Before another packaged replay, a disposable clone of the current 17,427-record
catalog was built from a read-only `metadata.db` copy with tiny placeholder managed
files. The exact production worker processed the complete currently eligible plan in
one persistent session: 1,840 planned, 1,840 successful, no failed chunk. A fresh
Calibre session found zero remaining eligible comma-form books and zero invalid managed
paths. The clone was removed. The complete 814-test suite also passes. Current live
state remains uncertain until mandatory Scan; packaging this qualified fix is pending.

The qualified package then completed 1,835 of 1,835 planned author operations in
299,040 ms. State is authoritative at revision 1,835 with no pending mutation or
uncertainty; the live catalog remains 17,427 books and 18,758 formats with no pipe-form
authors or invalid managed paths. Fresh read-only verification exposed five new
conservative comma-form candidates after shared-entity convergence. Three came from
safe one-pipe names whose stored sorts differed only in comma whitespace; two retained
the projected pipe spelling while live Calibre exposed an equivalent comma form.
Policy `author-name-normalization/1.2.0` accepts exact family/given equivalence after
NFC and whitespace canonicalization while still rejecting different names. The exact
five live records passed 5 of 5 through the production worker on a disposable catalog
clone, including canonical sorts and fresh managed paths, leaving zero candidates.
All temporary data was removed and the complete 816-test suite passes. At that point,
only a short Completed-state five-record packaged follow-up was pending; no workflow
replay was required.

The packaged follow-up completed all five edge records in 11,138 ms. Final state is
authoritative at revision 1,840 with no pending mutation or uncertainty. Fresh Calibre
read-only verification reports 17,427 books, 18,758 formats, zero remaining eligible
comma-form books, zero pipe-form books, and zero invalid managed paths. No mutation
worker, cover staging file, or transfer staging file remains. Final packaged restart
loaded authoritative revision 1,840. Installed Calibre opened the exact cleaned library
and remained responsive. After both GUIs closed, no matching process remained and
`metadata.db` accepted exclusive read access. Final post-close verification reports
zero missing format files, zero unexpected library temp files, zero staging files, and
zero qualification directories.

## Final outcome

Complete. The packaged application performed the one-time Exact, Candidate, metadata,
and final author/path cleanup on the backed-up disposable library; restart, persisted
state, Calibre-open, catalog/file, process, lock, and residue acceptance all passed.
