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
- [ ] Packaged release accepted on disposable library.
- [ ] Final Calibre-managed name normalization approved and implemented.

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
- [ ] Before destructive packaged acceptance, obtain an explicit caller-selected
  disposable representative library and explicit confirmation of its complete
  external backup. Do not infer either from earlier API-spike approval.
- [ ] With live provider requests disabled unless separately approved, execute and
  record the complete packaged Exact/Candidate/metadata/restart/Calibre-open workflow,
  actual timings, deviations, process/lock/temp state, and final technical baseline.
- [ ] Do not begin final filename/folder normalization or any step 9 implementation.

The selected package is framework-dependent `win-x64`: measured merged publish size
was 23.18 MiB/79 files versus 230.36 MiB/505 files self-contained. The one-time tool
already requires installed Calibre, so requiring the .NET 10 Desktop Runtime avoids a
roughly tenfold package expansion. The PDF worker remains in `pdf-worker/` because
flat merging exposed a same-named native dependency collision.

Package `CalibreLibraryCleaner-0.1.0-win-x64-framework-dependent.zip` contains 79
manifested payload files, is 9,308,644 bytes compressed, and has SHA-256
`fb11d863ef9dae39f698f84ac2e94ca712519bf639293cd2b5877a67c90c9f09`. Two complete
builds produced that identical hash. Direct packaged smoke passed in 8,031 ms with no
new lingering process or temporary extraction directory. Destructive packaged
acceptance remains pending explicit caller selection and external-backup confirmation.

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

## Final outcome

Pending implementation. Completion means the packaged application performs the
stated one-time cleanup, metadata enrichment, and Calibre-managed filename/folder
normalization safely on a disposable representative library.
