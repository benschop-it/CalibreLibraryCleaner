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
- Open Library currently confirms same-work identity only; it does not fetch or apply
  corrected metadata. Extend/reuse its provider boundary rather than confusing work
  evidence with metadata authority.
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
- Candidate cleanup currently transfers formats and removes source formats/records;
  it does not update metadata.
- Existing recommendations can choose a whole-record metadata source but do not
  provide online edition metadata or field-level proposal/application.
- Open Library search and reduced caching exist for unresolved same-work pairs.
- Google Books metadata lookup, edition fusion, cover download, credential UI/storage,
  metadata mutation, and final name normalization do not exist.
- Metadata and Expanded UI currently expose choices that do not drive Unified cleanup.
- WPF Scan and Exact cleanup use separate busy states and can potentially overlap.
- Release publish does not reliably include the required PDF worker.

## Proposed design

### Final workflow

```text
Scan -> Exact review/cleanup -> Candidate preparation
     -> Unified review + online metadata proposals
     -> final checked cleanup/metadata update
    -> Calibre name normalization -> done
```

Candidate preparation creates a metadata-review subject for every residual record:

- each Unified group becomes one subject whose target is its selected keeper;
- records outside Unified groups become singleton subjects;
- changing a group keeper changes only the update target, not the matched edition.

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
through Calibre's supported database API before deleting source records, then verify
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
6. **Build scalable metadata review.** Add proposed rows, filters, cover preview, and
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

No blocking product questions remain for steps 1-4.

Before metadata mutation (step 7), verify against the installed supported Calibre
version exactly which API calls update authors, title, identifiers, publisher, date,
language, series/index, and cover, and whether they implicitly rename managed paths.
If behavior differs from this plan, explain the consequence and ask the user before
changing scope.

Before name normalization (step 9), present the tested supported Calibre mechanism and
its observed effects for explicit user confirmation.

## Progress

- [x] One-time product goal and non-goals confirmed.
- [x] Baseline comparison `8849631` -> `fd336c1` reviewed.
- [x] Metadata review defaults and provider strategy confirmed.
- [x] Google Books API-key UI requirement confirmed.
- [ ] Product documentation reset completed.
- [ ] UI ambiguity and operation overlap removed.
- [ ] Open Library rich metadata proposals implemented.
- [ ] Google Books/key settings implemented.
- [ ] Provider fusion and scalable proposal review implemented.
- [ ] Approved metadata mutation implemented.
- [ ] Packaged release accepted on disposable library.
- [ ] Final Calibre-managed name normalization approved and implemented.

## Final outcome

Pending implementation. Completion means the packaged application performs the
stated one-time cleanup, metadata enrichment, and Calibre-managed filename/folder
normalization safely on a disposable representative library.
