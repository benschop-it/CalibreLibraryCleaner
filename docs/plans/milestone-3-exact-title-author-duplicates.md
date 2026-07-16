# Milestone 3 Exact Title/Author Duplicates

## Objective

Deliver the next read-only vertical slice of Calibre Library Cleaner. After a successful scan, the application deterministically normalizes each record's stored title and author names, builds an order-independent normalized author set, and identifies candidate duplicate book records whose normalized title and normalized author set are exactly equal.

The result is record-level metadata evidence only. It does not establish that ebook content, editions, or files are identical, and it never authorizes a merge, deletion, replacement, or other mutation. The WPF application must display these groups as a separate category from the exact binary file groups delivered in Milestone 2 and explain the different evidence behind each category.

The milestone is complete only when the normalization and grouping rules are directly unit tested, the existing end-to-end scan produces both result categories independently, the WPF view supports efficient keyboard review and lightweight filtering, and safety tests prove that metadata duplicate analysis makes no changes to `metadata.db` or any Calibre-managed file.

## Scope

- Normalize stored book titles with one documented, culture-independent algorithm.
- Normalize each stored author name with the same text primitives and author-specific eligibility rules.
- Represent multiple authors as a distinct, ordinally sorted normalized set so input author order does not affect identity.
- Build a normalized book identity from normalized title plus normalized author set.
- Group distinct Calibre book records only when both normalized identity components are exactly equal.
- Exclude singleton identities and records that cannot produce a usable normalized title and non-empty normalized author set.
- Assign collision-free, deterministic group identities derived from normalized identity rather than record order or runtime hash codes.
- Order groups and members deterministically.
- Store a stable reason code and human-readable explanation for every metadata group.
- Add metadata groups to the immutable `LibrarySnapshot` beside, not inside, Milestone 2 exact binary groups.
- Extend the existing scan use case and progress reporting to run metadata grouping before committing a successful snapshot.
- Add a separate WPF `Metadata candidates` tab with group and record detail, explicit candidate wording, normalized evidence, and a match-reason display.
- Add text filtering, previous/next group commands, and session-only defer state without redesigning the existing window or persisting review data.
- Preserve all Milestone 1 and 2 behavior, including read-only catalog access, streaming hashes, exact binary groups, cancellation, findings, and atomic UI result replacement.

## Out of scope

- Treating a metadata match as proof of identical content, the same edition, or redundant files.
- Automatically merging, deleting, replacing, moving, renaming, or otherwise modifying records or files.
- Reversing `Last, First` names, consulting `author_sort`, or reproducing Calibre's configurable author-sort algorithm.
- Removing subtitles or splitting titles into title/subtitle components.
- Removing initials, periods within initials, or other punctuation carrying stored information.
- Inferring aliases, pen names, transliterations, localized names, editors, translators, contributors, or name roles.
- ISBN or other identifier normalization/matching. Existing identifiers may remain available as contextual record data but do not affect identity, grouping, ordering, confidence, or filtering.
- Strong-identifier duplicate detection, fuzzy metadata matching, edit distance, token similarity, phonetics, stemming, article removal, series/year/edition inference, or title translation.
- EPUB or PDF inspection, text extraction, normalized content fingerprints, or malformed-ebook analysis.
- Quality scoring, recommendations, keeper selection, recoverable-space estimates, cleanup plans, approval, execution, backup, audit, or rollback.
- Persistent review/defer storage, settings, caches, database schema changes, or a broad duplicate-review workspace redesign.
- AI or network services.

## Relevant requirements

- `AGENTS.md`: analysis and Calibre-managed files remain read-only; algorithms are deterministic and explainable; project direction is preserved; no future milestone behavior is introduced.
- `PLANS.md`: this cross-project plan uses the required structure and must be updated with decisions, deviations, verification results, and final outcome during implementation.
- `docs/product-vision.md`: prioritize safety, local operation, explainability, and scalability to tens of thousands of records.
- `docs/functional-requirements.md`: exact normalized title/author matches are candidates rather than proof; every group exposes its confidence category and reasons.
- `docs/architecture.md` and ADR 0002: normalization, identity, invariants, and grouping belong in Domain; Application orchestrates the use case; WPF presents results; Infrastructure remains integration-only.
- `docs/domain-model.md`: record-duplicate groups require at least two distinct records and immutable values.
- `docs/duplicate-detection.md`: apply Unicode normalization, invariant case normalization, whitespace collapse, punctuation-spacing normalization, zero-width removal, and stable author ordering without removing subtitles, reversing names, discarding initials, or inferring editions/aliases.
- `docs/test-strategy.md`: cover normalization/grouping in Domain, orchestration/cancellation in Application, synthetic end-to-end integration and safety, architecture boundaries, and focused WPF navigation behavior.
- `docs/roadmap.md`: Milestone 3 is exact title/author grouping, reason/category display, keyboard navigation, and defer state; later matching, ebook assessment, scoring, recommendations, and cleanup remain deferred.
- ADR 0001: `metadata.db` stays in strict read-only/query-only access and receives no schema or data mutation.
- ADR 0003: no Calibre CLI or other mutation boundary is introduced.
- Nested Domain/Application/Infrastructure/WPF/test `AGENTS.md` files remain applicable.

## Existing implementation inspected

- The repository is at completed Milestone 2 (`205ac44`) with one unrelated modified Rider workspace file, `.idea/workspace.xml`; implementation must not alter or overwrite that user/IDE state.
- The solution targets .NET 10 with nullable references, latest stable C#, analyzers and code style as errors, deterministic builds, and central package management.
- Domain already contains immutable `CalibreBook`, `BookAuthor`, `LibrarySnapshot`, fingerprint types, and exact binary duplicate types. `CalibreBook.Authors` preserves Calibre link order and exposes both stored `Name` and `SortName`.
- `ExactBinaryDuplicateDetector` is a cancellation-aware pure Domain service. It groups file references by `(size, SHA-256)`, excludes singletons, sorts members by record/format/path, and sorts groups by size/digest.
- `LibrarySnapshot` currently contains `Books`, `Findings`, and `ExactBinaryDuplicateGroups`; it defensively copies all input collections.
- `ScanLibraryUseCase` revalidates the selected library, reads schema-27 metadata, safely resolves format paths, hashes files, maps findings, constructs `CalibreBook` records, invokes exact binary grouping, and commits one complete snapshot. Failed or canceled scans do not return partial snapshots.
- `ICalibreMetadataReader` already supplies exact stored title and ordered author-name values. No new SQLite query, schema field, filesystem operation, or Infrastructure port is needed for Milestone 3.
- The SQLite reader may produce a book with no usable author link after a broken reference and already records catalog issues. Blank author-sort values are findings, but author sort is not needed for metadata identity.
- `MainWindowViewModel` builds presentation rows off the dispatcher, applies collections with one reset notification, coalesces scan progress, and maintains separate selection for books and exact file groups.
- `MainWindow.xaml` has `Library` and `Exact file duplicates` tabs. The exact-file tab already states that byte equality is not a duplicate-record verdict and uses keyboard-navigable read-only `DataGrid` controls.
- Existing WPF rows join exact binary members back to `CalibreBook` display metadata. This lookup pattern can be reused for metadata-group member rows.
- Synthetic schema-27 fixtures and real Infrastructure composition already exercise end-to-end scans. Safety tests capture names, bytes/hashes, lengths, attributes, and timestamps before/after analysis and assert no SQLite sidecars are created.
- Architecture tests enforce project references, keep SQLite/filesystem/cryptography implementations out of core and WPF ViewModels, and confine WPF Infrastructure use to composition.
- No title/author normalizer, normalized identity, metadata duplicate group, metadata group display, filter, or defer state currently exists.

## Proposed design

### End-to-end flow and application boundary

1. Keep `ScanLibraryUseCase.ExecuteAsync` as the single user-visible analysis operation. Validation, read-only catalog loading, path resolution, hashing, findings, and exact binary grouping remain unchanged.
2. After the complete `CalibreBook` list has been materialized, report a new `GroupingExactMetadataDuplicates` phase and invoke the pure Domain metadata detector with the scan cancellation token.
3. The detector attempts to create one normalized identity per book, excludes ineligible identities, groups eligible records by exact normalized identity, removes groups with fewer than two distinct record IDs, and explicitly sorts all results.
4. Construct `LibrarySnapshot` with both `ExactBinaryDuplicateGroups` and `ExactMetadataDuplicateGroups`. Report `Completed` only after both lists exist.
5. `MainWindowViewModel` builds both presentation categories from that one snapshot and atomically replaces visible rows only after all presentation data is ready.

No new Infrastructure interface is proposed. The existing `ICalibreMetadataReader` is the only metadata source and is unchanged. No normalizer/detector interface is proposed in Application because normalization is deterministic Domain policy with no replaceable external boundary. Hiding it behind an interface would add indirection without improving isolation. Application remains responsible for cancellation/progress orchestration; Domain remains responsible for identity and grouping rules.

### Proposed normalized identity Domain types

Add the following immutable, integration-free types under `CalibreLibraryCleaner.Domain.Duplicates`:

- `NormalizedTitle`: non-empty canonical title text with ordinal value equality.
- `NormalizedAuthorName`: non-empty canonical author-name text with ordinal value equality.
- `NormalizedAuthorSet`: a defensively copied, duplicate-free, ordinally sorted collection of `NormalizedAuthorName`. Equality and hashing are by the ordered values, never collection reference identity or current culture.
- `NormalizedBookIdentity`: the composite of one `NormalizedTitle` and one non-empty `NormalizedAuthorSet`.
- `MetadataTextNormalizer`: the single implementation of the title/author text pipeline. It exposes explicit `TryNormalizeTitle` and `TryNormalizeAuthorName`/`TryCreateAuthorSet` operations so empty post-normalization values do not become valid identity values.
- `ExactMetadataDuplicateGroupId`: a strongly typed ID derived solely from `NormalizedBookIdentity`. Its canonical representation is versioned and UTF-8-byte-length-prefixed (`exact-metadata:v1` plus title and ordered author components), so it is deterministic and delimiter-safe without a collision-prone runtime/non-cryptographic hash. It never uses member IDs or enumeration order.
- `ExactMetadataDuplicateMatchReason`: fixed evidence containing reason code `EXACT_NORMALIZED_TITLE_AUTHOR_SET`, category label `Exact normalized metadata`, and the explanation that normalized title and order-independent normalized author set are exactly equal.
- `ExactMetadataDuplicateGroup`: contains ID, normalized identity, match reason, and at least two distinct `CalibreBookId` members. It defensively copies members and rejects duplicates/singletons.
- `ExactMetadataDuplicateDetector`: cancellation-aware Domain service that creates identities, groups records, and returns deterministic immutable results.

`LibrarySnapshot` gains `IReadOnlyList<ExactMetadataDuplicateGroup> ExactMetadataDuplicateGroups` as a separate property. The constructor continues to copy inputs. Existing exact binary types and equality remain intact.

The group ID is an internal stable identity, not a user-facing claim and not a persistence format commitment. It should not be logged because its canonical form contains normalized metadata. WPF displays the reason and normalized evidence, not the raw canonical ID.

### Shared text normalization pipeline

Apply exactly the following pipeline independently to a title and to each author name:

1. Reject `null`; accept the stored string as data without parsing it into semantic components.
2. Normalize to Unicode Normalization Form C (NFC). NFC is deliberately conservative: canonically equivalent composed/decomposed text compares equally, while compatibility characters such as full-width forms or ligatures are not broadly folded together.
3. Apply `ToUpperInvariant()` to the whole string, then normalize to NFC again. This is the documented .NET invariant case-normalization operation; it never uses `CurrentCulture` or the OS/UI language.
4. Enumerate Unicode scalars and discard characters in `UnicodeCategory.Format`, including zero-width and directional formatting controls. Do not remove letters, marks, digits, punctuation, or symbols.
5. Convert every Unicode whitespace run recognized by the runtime to one ASCII space and trim leading/trailing space.
6. Normalize punctuation spacing in a second deterministic pass: remove the canonical ASCII space immediately before or after any Unicode punctuation scalar. This makes `Title : Subtitle` and `Title:Subtitle` equal while retaining the colon and all subtitle text.
7. Normalize the completed value to NFC and reject it if it is empty.

Punctuation means the Unicode connector, dash, open, close, initial-quote, final-quote, and other-punctuation categories. Punctuation code points and repetition are preserved. Symbols are not rewritten. No smart-quote, dash, apostrophe, ampersand, numeral, or romanization equivalence table is introduced.

Consequences that must be documented and tested:

- `The Book : A Tale` and `the  book:  a tale` share `THE BOOK:A TALE`.
- `The Book: A Tale` does not match `The Book`; subtitles are retained.
- `J. R. R. Tolkien` can normalize spacing to `J.R.R. TOLKIEN`, but it does not match `J R R Tolkien` or `JRR Tolkien`; initials and their punctuation are not discarded.
- `Doe, Jane` remains `DOE,JANE` and does not match `Jane Doe`; names are never reversed.
- Hyphen, en dash, and em dash remain different unless NFC itself defines equivalence.
- Author aliases, name roles, and identifiers have no effect.

### Title normalization rules

- Normalize only `CalibreBook.Title` using the shared pipeline.
- Do not consult file names, series, identifiers, comments, embedded metadata, or Calibre sort fields.
- Do not remove leading articles, subtitles, bracketed edition text, volume numbers, punctuation, or parenthetical text.
- A title that becomes empty after zero-width/whitespace processing makes the record ineligible for metadata grouping. It is never grouped on an empty key.

### Author normalization and multiple authors

- Normalize only each `BookAuthor.Name`. Never use `BookAuthor.SortName` or the book-level `AuthorSort`, because those may be stored in `Last, First` order and may reflect Calibre/user locale configuration.
- If a book has no authors, it is ineligible for metadata grouping. Matching a title alone is not permitted.
- If any stored author name cannot produce a non-empty normalized name, the whole record is ineligible. The detector must not silently discard that author and accidentally create a weaker identity.
- Treat normalized authors as a mathematical set: remove exact duplicate normalized names, then sort with `StringComparer.Ordinal`. Thus `[Alice, Bob]`, `[Bob, Alice]`, and `[Alice, Alice, Bob]` produce the same author set.
- Author IDs and original link order do not affect identity. They remain available in the original record for display/context.
- Author sets must be exactly equal. A record listing only a main author does not match a record listing that author plus additional authors; inferring that the smaller set is sufficient would be a subset/role heuristic outside Milestone 3.
- Do not special-case literal strings such as `Unknown`, `Unknown Author`, or localized equivalents. Inferring which stored values are semantic placeholders would be heuristic and locale-dependent. A literal non-empty stored value is normalized as ordinary text; an absent/empty post-normalization author is excluded as described above.

### Exact grouping, identity, reason, and ordering

- Equality key: `(NormalizedTitle, NormalizedAuthorSet)` only.
- A metadata group requires at least two distinct `CalibreBookId` values. Multiple files on one record can never create a metadata group.
- Identifiers, formats, hashes, relative paths, author IDs, author-sort values, series, language, and record insertion order do not affect grouping.
- Member records are ordered by `CalibreBookId.Value` ascending.
- Groups are ordered by normalized title ordinal, then lexicographically by the ordered normalized author-name sequence, then by canonical group ID ordinal as a final total-order tie breaker.
- Dictionary/hash-set iteration order and `GetHashCode()` values must never determine output order or IDs.
- Every group stores `EXACT_NORMALIZED_TITLE_AUTHOR_SET` and the normalized title/author evidence. The WPF reason text is derived from the stored reason, not reverse-engineered from display rows.
- The category is qualitative (`Exact normalized metadata candidate`), not a numeric probability. It remains weaker evidence than exact binary equality and does not imply content equality.

### Interaction with Milestone 2 exact binary results

The two detectors operate independently over the same immutable books and produce separate snapshot collections:

| Scenario | Exact file groups | Metadata groups | Interpretation |
| --- | --- | --- | --- |
| Same title/authors, different bytes | None for those files | Present | Candidate duplicate records; content may differ. |
| Different title/authors, identical bytes | Present | None | Byte-identical managed files; records are not asserted to match. |
| Same title/authors and identical bytes | Present | Present | Two independent facts shown in separate views; no automatic combined verdict. |
| Missing/inaccessible format, same title/authors | No accepted file member for that format | Present | Metadata grouping is independent of file readability. |

Do not merge these collections, upgrade a metadata group's category because of binary overlap, deduplicate one against the other, calculate a combined confidence, or choose a preferred record. The existing exact-file reason remains equal size plus SHA-256. The WPF tabs, headings, summaries, reason text, and automation names must consistently use `file` versus `record metadata` terminology.

### WPF ViewModels and minimal view changes

Add:

- `MetadataDuplicateGroupRowViewModel`: group ID for internal state, normalized title, normalized author display, original record count, category/reason text, search text, session defer state, and immutable member rows.
- `MetadataDuplicateMemberRowViewModel`: record ID, original title, original authors, author sort as context, declared formats, and optional existing identifier display. Identifier text is contextual only and must be labeled so it cannot be mistaken for grouping evidence.
- `MetadataDuplicateFilterMode`: `All`, `Active`, and `Deferred` presentation modes.

Extend `MainWindowViewModel` with:

- A complete in-memory metadata-group presentation list and a separately exposed bulk-updated filtered collection.
- `SelectedMetadataDuplicateGroup` and `SelectedMetadataDuplicateMembers`.
- `MetadataDuplicateFilterText` using ordinal-ignore-case substring matching over original/normalized title, original/normalized authors, and record IDs. Filtering never changes Domain grouping.
- `MetadataDuplicateFilterMode`, a summary showing visible/total/deferred counts, and an empty-state message that distinguishes no matches from a filter hiding matches.
- `NextMetadataDuplicateGroupCommand` and `PreviousMetadataDuplicateGroupCommand`, wrapping within the currently visible list only when non-empty.
- `ToggleMetadataDuplicateDeferredCommand`. Defer state is WPF-only and in memory, keyed by `(LibraryUuid, ExactMetadataDuplicateGroupId)` so a successful rescan of the same library can retain matching session decisions. It is never persisted and never enters `LibrarySnapshot` or Calibre data.
- Selection repair after filtering/defer changes: retain the selected group if visible; otherwise select the first visible group; expose an empty member list when none is visible.

Extend `MainWindow.xaml` with one new `_Metadata candidates` tab rather than redesigning the shell. The tab contains:

- Explicit text: `These records have exactly equal normalized titles and author sets. This is metadata evidence only; content and editions may differ.`
- A labeled filter text box with access key and a small All/Active/Deferred selector.
- Previous, Next, and Defer/Restore buttons bound to commands, with keyboard shortcuts documented in tooltips/automation help text. Proposed shortcuts are `Ctrl+P`, `Ctrl+N`, and `Ctrl+D` while the window is active.
- A read-only group grid showing normalized title, normalized author set, record count, category, reason, and deferred state.
- A read-only selected-record grid showing original record metadata/context.

Standard `DataGrid` arrow, Page Up/Down, Home/End, and tab navigation remain available. Use access keys, explicit tab order where needed, automation names, text labels (not color-only states), and bindings; no review business logic belongs in code-behind. The existing Library and Exact file duplicates tabs remain functionally unchanged.

Presentation materialization stays on the existing background `Task.Run` path with cancellation checks. Apply each observable collection with one reset notification. The filter binding may use a short WPF `Delay` (proposed 200 ms) to avoid refreshing on every rapid keystroke in a very large result set.

### Performance and memory with large libraries

- Normalize each title and author once per scan. Do not normalize pairwise or inside equality comparisons.
- Identity creation is O(total title/author Unicode scalar count). Author-set sorting is O(a log a) per record, where `a` is normally small.
- Group eligible records in a dictionary keyed by `NormalizedBookIdentity`, giving expected O(n) grouping rather than O(n squared) record comparison.
- Sort only final members/groups for deterministic output: O(n log n) in the worst case and O(g log g) for `g` groups.
- Retain only normalized strings, identity/group/member records, and WPF row metadata. Do not copy ebook bytes, query SQLite again, or create a persisted index.
- Explicitly check cancellation between records, authors, group construction, member ordering/materialization, and snapshot/presentation creation. A single framework sort cannot be interrupted internally, but work around it is bounded and checked.
- Add a generated Domain scale test with at least 50,000 records, repeated identities, multiple authors, and shuffled input. Assert group count/order and linear-sized output; collect timing/memory only as diagnostics, not brittle pass/fail thresholds.
- Filtering is O(g) per refresh over metadata groups, not all pairwise records. Use delayed input and bulk reset; do not create one collection-change event per row.

## Files expected to change

The implementation should use the following concrete file set. Any deviation should be recorded in this plan before implementation continues.

### Documentation

- `docs/plans/milestone-3-exact-title-author-duplicates.md` — this plan; keep progress, decisions, deviations, verification, and outcome current.
- `docs/domain-model.md` — add normalized identity and exact metadata group types/invariants beside exact binary file groups.
- `docs/duplicate-detection.md` — record the exact NFC/case/zero-width/whitespace/punctuation algorithm, author-set rules, identity/order/reason rules, and binary-versus-metadata distinction.

No package-version, solution, project-reference, schema-support, or ADR change is expected.

### Domain

- `src/CalibreLibraryCleaner.Domain/Libraries/LibrarySnapshot.cs`
- `src/CalibreLibraryCleaner.Domain/Duplicates/NormalizedTitle.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/NormalizedAuthorName.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/NormalizedAuthorSet.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/NormalizedBookIdentity.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/MetadataTextNormalizer.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactMetadataDuplicateGroupId.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactMetadataDuplicateMatchReason.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactMetadataDuplicateGroup.cs` — new.
- `src/CalibreLibraryCleaner.Domain/Duplicates/ExactMetadataDuplicateDetector.cs` — new.

Existing `ExactBinaryDuplicate*` files are not expected to change.

### Application

- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanPhase.cs` — add the metadata-grouping phase.
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs` — invoke metadata grouping, preserve cancellation, and populate both snapshot collections.

No Application abstraction/interface or Infrastructure production file is expected to change.

### WPF

- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateGroupRowViewModel.cs` — new.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateMemberRowViewModel.cs` — new.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateFilterMode.cs` — new.

`App.xaml.cs`, `MainWindow.xaml.cs`, existing exact-file row ViewModels, and WPF project/package references are not expected to change.

### Tests and synthetic fixtures

- `tests/CalibreLibraryCleaner.Domain.Tests/Duplicates/MetadataTextNormalizerTests.cs` — new.
- `tests/CalibreLibraryCleaner.Domain.Tests/Duplicates/ExactMetadataDuplicateDetectorTests.cs` — new.
- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibrarySnapshotTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ScanLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticCalibreLibrary.cs` — allow caller-supplied titles and multiple author names for generated records.
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Analysis/ExactMetadataDuplicateAnalysisTests.cs` — new end-to-end real SQLite/path/hash/use-case interaction tests.
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Safety/ReadOnlyLibraryScanSafetyTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/MainWindowTests.cs`

Existing SQLite reader and hashing tests should remain unchanged except for mechanical fixture call-site adjustments if the fixture signature is extended compatibly.

## Safety considerations

- Metadata normalization/grouping consumes only the already loaded immutable `CalibreBook` values. It performs no SQLite, filesystem, stream, process, or network access.
- `metadata.db` remains opened only by the existing read-only/query-only Infrastructure reader. No SQL statement, schema contract, writable connection, or database package changes.
- Existing file hashing remains read-only Milestone 2 behavior. Metadata grouping adds no file open and does not inspect EPUB/PDF structure or content.
- Empty/unusable author data never degrades into title-only matching. This avoids broad false-positive groups.
- Exact metadata groups are always labeled candidates and never feed mutation, recommendation, score, plan, or AI behavior.
- Session defer/filter/selection state exists only in WPF memory. It cannot modify the snapshot or selected library and disappears when the application exits.
- UI copy must not contain `identical content`, `safe to merge`, `redundant record`, `keeper`, or `delete` language for metadata groups.
- Normalized group IDs contain metadata in canonical form and therefore are not logged or persisted by default.
- Extend full-library before/after manifests to a successful metadata-only match, binary-only match, combined independent match, empty-author exclusion, filtering/defer UI operations where applicable, and cancellation during metadata grouping. Assert database/file bytes, timestamps, attributes, names, locations, and sidecar absence are unchanged.
- Synthetic fixtures remain under test-owned temporary directories and never use a real user library.
- Review production source for writable SQL/filesystem/process APIs and future-scope terms before completion.

## Implementation steps

1. Resolve and record the normalization/defer questions below before production edits; update Progress.
2. Add normalized title/author/set/book identity values and the one shared normalization pipeline, with direct culture/Unicode/punctuation/forbidden-heuristic tests.
3. Add deterministic metadata group ID/reason/group invariants and the cancellation-aware detector, including shuffled-input and 50,000-record scale coverage.
4. Extend `LibrarySnapshot` with a defensively copied metadata-group collection and update constructor call sites/tests without weakening exact binary behavior.
5. Add `GroupingExactMetadataDuplicates` to progress and invoke the detector in `ScanLibraryUseCase` after book materialization, preserving cancellation and atomic success semantics.
6. Extend Application tests for metadata-only, binary-only, both, neither, ineligible authors/titles, deterministic ordering, progress, and cancellation.
7. Extend synthetic fixtures for caller-defined metadata and add real-Infrastructure integration tests proving stored name semantics and independent binary/metadata result matrices.
8. Add metadata group/member/filter ViewModels, in-memory defer keys, filtered selection/navigation commands, summaries, and bulk/cancellable presentation creation.
9. Add the minimal Metadata candidates tab, explicit safety wording, reason/evidence fields, access keys, shortcuts, automation names, and filter/defer controls. Keep code-behind view-only.
10. Extend WPF tests for display, distinction, filtering, navigation, defer/restore, rescan retention by library/group ID, collection reset counts, cancellation, and XAML startup/binding activation.
11. Extend architecture guards and full-library safety tests. Confirm no Infrastructure production change, writable access, parsing, fuzzy/identifier matching, scoring, recommendation, or cleanup behavior entered the slice.
12. Run focused tests, the full verification sequence, dependency vulnerability audit, WPF startup/graceful-close smoke test, and a manual keyboard/accessibility review against a generated synthetic library only.
13. Review the complete diff and update this plan with deviations, failed approaches, exact test counts/command results, residual risks, and Final outcome.

## Tests

### Domain unit tests

- NFC makes canonically composed/decomposed title and author text equal; compatibility-only variants remain distinct.
- Results remain identical under different `CurrentCulture`/`CurrentUICulture` settings, including Turkish culture cases covered by `ToUpperInvariant`.
- Unicode whitespace collapses and trims; Unicode format/zero-width characters are removed.
- Spaces adjacent to every Unicode punctuation category normalize consistently while punctuation code points/repetition remain present.
- Subtitle text, leading articles, bracketed/parenthetical edition text, numerals, symbols, initials, and punctuation are retained.
- `Doe, Jane` does not equal `Jane Doe`; initial-bearing and initial-discarded names do not match; alias/pen-name strings do not match.
- Empty post-normalization title, no authors, or any empty post-normalization author makes a record ineligible.
- Literal `Unknown` is treated as ordinary text and is not inferred as a sentinel.
- Multiple-author order and duplicate links do not affect the set; different membership does.
- `NormalizedAuthorSet` and snapshot/group constructors defensively copy inputs and have value-based equality.
- Equal normalized title/author sets group; equality of only one component does not.
- Singleton groups are absent and group constructors reject fewer than two distinct record IDs.
- Member/group IDs and ordering are identical for shuffled input and independent of culture/runtime dictionary order.
- Group reason code/category/evidence is populated exactly.
- Cancellation is observed during identity creation, author processing, grouping, and final materialization.
- A generated 50,000-record dataset produces expected deterministic groups without pairwise comparison or hard wall-clock assertions.

### Application unit tests

- Validation/catalog/path/hash failures retain existing behavior and never return a partial metadata result.
- A successful scan returns both separate snapshot collections.
- Same metadata/different fingerprint produces only a metadata group; different metadata/same fingerprint produces only an exact file group; same metadata/same fingerprint produces one in each collection.
- Missing/inaccessible/changed/invalid formats do not prevent metadata grouping and remain excluded from exact file grouping.
- Author sort, author IDs/order, formats, identifiers (including matching/conflicting ISBN), and hashes do not affect metadata identity.
- Metadata group order and IDs remain stable when catalog input order changes.
- `GroupingExactMetadataDuplicates` reports start/completion before `Completed`; `Completed` occurs exactly once.
- Cancellation at metadata grouping/materialization propagates, suppresses `Completed`, and returns no successful partial snapshot.
- Existing exact binary and finding tests remain green.

No fake normalizer port is required; tests use the real Domain policy through the use case and reserve direct rule coverage for Domain tests.

### Infrastructure integration tests

- A generated schema-27 library with titles differing only by allowed normalization and authors in opposite link order produces one metadata group through the real reader/path resolver/hasher/use case.
- Original author `name` values, not stored author-sort values, determine the group.
- `Last, First` and `First Last`, subtitle/no-subtitle, initials/no-initials, and distinct punctuation remain ungrouped as specified.
- No-author/broken-author records are excluded rather than grouped by title alone.
- The four binary/metadata interaction rows in the design table are covered with generated files.
- Matching or conflicting ISBN values do not change groups.
- Existing SQLite, path, and streaming hash integration behavior remains unchanged.

### WPF/ViewModel tests

- Successful scan shows a separately labeled Metadata candidates tab collection, selects the first visible metadata group, and displays original record metadata plus normalized evidence/reason.
- Safety text states that metadata equality does not imply identical content or edition equivalence.
- Exact binary group rows and metadata group rows remain separate when records appear in both.
- Text filtering is ordinal-ignore-case, updates visible/total summaries, distinguishes no groups from no filter matches, and repairs selection.
- All/Active/Deferred filtering and Defer/Restore work without changing the Domain snapshot.
- Session defer survives a matching rescan of the same library/group ID, does not cross library UUIDs, and is not persisted.
- Previous/Next commands traverse only visible groups, wrap predictably, and disable when no group is visible.
- Bulk snapshot/filter application does not emit per-row collection changes.
- Failed/canceled rescan retains clearly labeled previous results and prior session state; successful replacement stays atomic.
- `MainWindow` constructs/shows/closes on STA with all new bindings and input bindings active.

Manual XAML review covers access keys, `Ctrl+P`/`Ctrl+N`/`Ctrl+D`, DataGrid arrow/Page/Home/End behavior, focus order, screen-reader names/help text, copyable reason/evidence, resize/high DPI, and state distinctions that do not rely on color.

### Architecture tests

- Existing production project-reference direction remains unchanged.
- Domain normalization/grouping code references no SQLite, filesystem, WPF, logging/DI, crypto implementation, process, or ebook-parser type/package.
- Application still references only Domain and contains no concrete integration/UI implementation.
- No Infrastructure production file is added or changed for metadata grouping.
- WPF ViewModels reference only Application/Domain/presentation types and no Infrastructure, SQLite, filesystem, parser, or process APIs.
- WPF Infrastructure namespace use remains confined to `App.xaml.cs`.
- A source guard confirms fuzzy/identifier/content matching and recommendation/cleanup types are absent from Milestone 3 production additions.

### Safety tests

- Capture strict recursive manifests before/after successful metadata-only, binary-only, and combined analyses.
- Repeat non-mutation assertions for no-author exclusion and cancellation during metadata grouping.
- Assert `metadata.db` bytes/hash and timestamps are unchanged and no journal/WAL/SHM/cache/temp file appears.
- Assert all format bytes, sizes, timestamps, attributes, names, and paths are unchanged.
- Exercise WPF filter/defer/navigation against in-memory results and assert no integration port is called as a consequence.
- Search production source for SQL mutation verbs; mutable file/directory APIs; Calibre CLI; EPUB/PDF libraries; ISBN grouping; fuzzy terms/algorithms; scoring; recommendations; cleanup/merge/delete execution.

## Verification commands

Run focused tests during implementation:

```powershell
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj --filter "FullyQualifiedName~Metadata|FullyQualifiedName~Normalized"
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --filter "FullyQualifiedName~ScanLibraryUseCase"
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MetadataDuplicate|FullyQualifiedName~Safety"
dotnet test tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj
```

Then run the required full sequence and package audit:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
dotnet list package --vulnerable --include-transitive
```

Review repository, architecture, and safety surfaces:

```powershell
git status --short
git diff --check
git diff --stat HEAD
git diff HEAD
rg -n --glob 'src/**/*.cs' 'CommandText\s*=|SqliteOpenMode|query_only' src
rg -n --glob 'src/**/*.cs' -e 'File\.(Write|Delete|Move|Copy|Create|OpenWrite|Replace)' -e 'Directory\.(Create|Delete|Move)' -e 'FileMode\.(Create|Append|OpenOrCreate|Truncate)' -e '\b(INSERT|UPDATE|DELETE|DROP|ALTER|REPLACE|VACUUM|ATTACH|DETACH)\b' -e 'calibredb|VersOne|PdfPig|Levenshtein|Jaro|fuzzy|recommendation|cleanup plan|merge|delete record' src
```

Start the built WPF executable, verify it remains alive through startup, request graceful close, and require empty standard error. If an interactive session is available, use only a generated synthetic library to verify category wording, filter, navigation, defer/restore, selection, resize/high-DPI, and accessibility behavior.

Do not report any command as successful until it actually completes. Record exact results, warnings, failures, manual limitations, and diff/safety review findings in Progress and Final outcome.

## Risks

- Any normalization can create false positives or false negatives. Conservative NFC and punctuation preservation favor false negatives over aggressive equivalence, but case/spacing normalization can still group records that represent different editions.
- `.NET` invariant case behavior can evolve with Unicode/runtime data across future runtimes. .NET 10 is the pinned target; tests must lock representative behavior and group IDs should be treated as versioned by the normalization policy.
- Removing all Unicode `Format` characters can concatenate text that a producer intended to separate. This follows the documented zero-width-removal rule and must be covered by examples.
- Punctuation-spacing normalization treats spacing around punctuation as insignificant while preserving the punctuation itself. Some languages use punctuation spacing semantically; UI candidate wording remains essential.
- Treating duplicate normalized author names as a set can conceal duplicate catalog links, but it implements the required author-set semantics and does not discard distinct initials/names.
- Literal placeholder authors such as `Unknown` may create broad candidate groups. Special-casing them would require locale/user heuristics, so the proposed conservative rule treats them as stored metadata and documents the limitation.
- The collision-free canonical group ID can be long and contains normalized metadata. It must remain internal, unlogged, and unpersisted in this milestone.
- Building normalized/presentation strings for tens of thousands of records adds O(n) memory above Milestone 2. Scale tests and background/bulk WPF materialization must guard against dispatcher stalls.
- Session-only defer state may surprise users expecting persistence. The UI should avoid implying it is saved; persistence needs a separately designed location, privacy policy, and lifecycle.
- Keyboard shortcuts can conflict with control-local behavior or accessibility conventions. Verify routing/focus manually and keep button/access-key alternatives.
- Existing hashing dominates scan time and can fail a whole scan at its established fatal boundary before metadata results are committed. Milestone 3 preserves atomic scan semantics rather than creating a second partial-result path.
- The working tree contains unrelated Rider workspace changes that must be preserved during implementation and excluded from diff claims.

## Unresolved questions

1. **NFC or compatibility normalization (NFKC)?** Proposed answer: NFC. It equates canonical Unicode representations without broadly equating compatibility characters; this is safer for an exact-candidate milestone.
2. **Which invariant case operation?** Proposed answer: whole-string `ToUpperInvariant()` followed by NFC, explicitly versioned as normalization policy v1. Do not implement a custom Unicode/locale name-folding table.
3. **How should records with absent or unusable authors behave?** Proposed answer: exclude the record from metadata grouping; never fall back to title-only identity. Treat non-empty literal `Unknown` values as ordinary stored text because sentinel inference is heuristic.
4. **Should duplicate normalized author names within one record remain duplicated?** Proposed answer: no. The requirement is an author set, so deduplicate exact normalized names and sort ordinally. Distinct initials/names remain distinct.
5. **Should group IDs be short hashes?** Proposed answer: no. Use the normalized composite identity with a versioned length-prefixed canonical representation, avoiding crypto in Domain and avoiding collision-prone stable hashes. Do not display/log the raw ID.
6. **Should metadata and exact binary evidence be combined?** Proposed answer: no. Preserve independent collections/views and test all intersections. Combined confidence/recommendations belong to later milestones.
7. **How durable is defer state?** Proposed answer: session-only WPF memory keyed by library UUID and deterministic group ID, retained across successful rescans in the same process and clearly not persisted.
8. **Should identifiers appear in metadata-member detail?** Proposed answer: they may be displayed as existing context if the minimal grid remains readable, but must be labeled contextual and must never enter grouping/filter category/reason. Omit the column if it harms the minimal layout.

These proposed answers keep the slice deterministic, conservative, read-only, and bounded to Milestone 3. Any change that introduces compatibility/fuzzy/identifier matching, persistence, content inspection, scoring, recommendations, or mutation requires separate approval and plan revision.

Approved resolution: all proposed answers above were accepted for implementation on 2026-07-15. Exact author-set equality also means that a main-author-only record and a record containing additional authors do not match in Milestone 3.

## Progress

- [x] Read root `AGENTS.md`, `PLANS.md`, and every nested `AGENTS.md` under `src/` and `tests/`.
- [x] Read the requested product vision, functional requirements, architecture, domain model, duplicate detection, test strategy, and roadmap documents.
- [x] Read every accepted ADR under `docs/adr/`.
- [x] Read the completed Milestone 0, 1, and 2 plans, including decisions, deviations, risks, and final outcomes.
- [x] Inspect the current solution/build/package configuration, git state, Domain/Application/Infrastructure/WPF implementation, XAML, relevant unit/integration/UI/architecture/safety tests, and synthetic fixtures.
- [x] Create the Milestone 3 execution plan.
- [x] Resolve and record the unresolved design questions.
- [x] Implement Domain normalization and exact metadata grouping.
- [x] Integrate metadata groups into the Application scan/snapshot.
- [x] Add minimal WPF display, filter, navigation, and session defer state.
- [x] Add/extend unit, integration, architecture, WPF, performance-oriented, and safety tests.
- [x] Run focused and full verification commands and package audit.
- [x] Complete WPF process/manual smoke review and full diff/safety review.
- [x] Update Final outcome.

### Post-completion review follow-up (2026-07-16)

- [x] Exclude records with `AUTHOR_REFERENCE_MISSING` findings from metadata grouping without hiding the records or findings from the snapshot.
- [x] Change prominent WPF labels and summaries from metadata duplicates to metadata candidates.
- [x] Add direct author normalization, mixed-invalid-author, punctuation, deterministic multi-author scale, real-reader conservative matching, and missing-author-reference tests.
- [x] Add strict non-mutation safety scenarios for combined binary/metadata evidence and no-author exclusion.
- [x] Run focused and full verification, review the complete diff, and record results below.

Implementation notes:

- The first focused Domain build failed analyzer rule CA1036 because `NormalizedAuthorSet` implemented `IComparable` without relational operators. Ordering was moved to an explicit detector comparer while the author set retains structural value equality; no analyzer was suppressed.
- The initial focused Domain test run then passed 32 tests, including the generated 50,000-record scale case.
- The first full `dotnet test --no-build` run passed Application (15), Infrastructure (39), Architecture (12), and WPF (7) but failed one Domain malformed-UTF-16 case: rune enumeration substituted `U+FFFD` for an unpaired surrogate. The normalizer now rejects ill-formed UTF-16 explicitly before normalization; the full required sequence must be rerun.
- The next full run showed that xUnit theory-data serialization itself replaces an unpaired surrogate with `U+FFFD`, so the malformed-input assertion never received malformed UTF-16. That case now constructs the string inside a non-serialized fact; production validation remains unchanged.
- The first architecture-test compile after adding the future-scope source guard used a FluentAssertions string overload that does not accept `StringComparison`. The guard now compares one invariant-uppercase source string; no production behavior or analyzer rule changed.
- `dotnet format` was run before the final verification sequence to normalize newly patched C# line endings and formatting. The final `dotnet format --verify-no-changes` then succeeded.
- The pre-review full test run passed 106 tests: Domain 33, Application 15, Infrastructure 39, Architecture 12, and WPF 7.
- The hidden WPF process stayed alive through startup, but `CloseMainWindow` did not close the process within five seconds under `Start-Process -WindowStyle Hidden`; the smoke harness killed it and reported the limitation. The STA `MainWindowTests.WindowCanBeShownWithReadOnlyViewModelBindings` test passed and directly constructs, shows, and closes the window. An interactive high-DPI/screen-reader review was not available in this session.
- Complete diff review excluded the pre-existing `.idea/workspace.xml` modification and confirmed no Infrastructure production file, package, project reference, schema contract, or Application port changed. The production mutation/future-scope scan returned no matches.
- During the 2026-07-16 review follow-up, the first build failed because a new FluentAssertions expression-tree predicate used the null-propagating operator. The assertion was changed to an explicit nullable-value check; no production behavior changed.
- Review-follow-up focused tests passed: Domain 35, Application 14, Infrastructure/safety 20, and WPF ViewModel 6.
- Review-follow-up verification succeeded: restore; build with zero warnings/errors; 124 full tests (Domain 46, Application 16, Infrastructure 43, Architecture 12, WPF 7); format verification; package vulnerability audit; whitespace check; and production mutation scan.

## Final outcome

Completed on 2026-07-15; review corrections verified on 2026-07-16.

- Added immutable normalized title, author name, author set, and book identity values; a deterministic NFC/invariant-uppercase/Unicode-format/whitespace/punctuation-spacing normalizer; a versioned length-prefixed group ID; a fixed exact-metadata reason/category; and cancellation-aware exact metadata grouping.
- Exact metadata groups require at least two distinct records with equal normalized title and complete order-independent normalized author set. Records with empty/unusable titles, no authors, or any unusable author are excluded. Main-author-only and all-authors records remain distinct.
- Records with a catalog `AUTHOR_REFERENCE_MISSING` finding are also excluded from metadata grouping because their complete author set is unknown; the records and findings remain visible in the snapshot.
- Original titles, author names/order, author-sort values, identifiers, formats, and paths remain unchanged in `CalibreBook` and are shown as context. `BookAuthor.Name` alone supplies author identity; author-sort and identifiers never influence grouping.
- Extended the existing atomic scan with a `GroupingExactMetadataDuplicates` phase and a separate `LibrarySnapshot.ExactMetadataDuplicateGroups` collection. Milestone 2 exact binary groups are unchanged and remain an independent collection/evidence category.
- Added a separate WPF Metadata candidates tab with explicit candidate/content warnings, normalized evidence and match reason, original record context, delayed text filtering, All/Active/Deferred views, previous/next keyboard commands, and session-only defer state keyed by library UUID plus deterministic group ID. Filtering/navigation/defer invoke no integration port and persist nothing.
- Added Domain normalization/invariant/grouping/cancellation/50,000-record tests; Application independent-signal/progress/cancellation tests; real synthetic SQLite interaction tests for metadata-only, binary-only, both, no-author, identifier conflict, author order/sort, and author-subset cases; WPF filtering/navigation/defer/rescan/library-isolation tests; architecture guards; and strict before/after non-mutation safety tests including cancellation during metadata grouping.
- Updated `docs/domain-model.md` and `docs/duplicate-detection.md` with the implemented invariants, exact rules, ordering, identity, reason, and file-versus-record distinction.
- Final `dotnet restore` succeeded.
- Final `dotnet build --no-restore` succeeded with zero warnings and zero errors.
- Final `dotnet test --no-build` succeeded: 124 passed, 0 failed, 0 skipped.
- Final `dotnet format --verify-no-changes` succeeded.
- `dotnet list package --vulnerable --include-transitive` reported no known vulnerable packages in any project.
- `git diff --check HEAD` completed without whitespace errors. Complete changed-file review found only the intended Domain/Application/WPF/docs/test changes; the unrelated Rider workspace modification remains untouched.
- Production SQL review still shows only the existing fixed `SELECT`, count, and read-only `PRAGMA` statements with `SqliteOpenMode.ReadOnly` and `query_only`. The mutation/future-scope scan found no writable file/directory calls, SQL mutation verbs, Calibre CLI, ebook parser, fuzzy matcher, recommendation, or cleanup implementation.
- Deviations: `NormalizedAuthorSet` is an immutable class with explicit structural equality rather than a record whose collection property would compare by reference; malformed UTF-16 is explicitly rejected as an additional invalid-input guard; and process-level graceful close could not be confirmed for a hidden WPF process, although the automated STA show/close test passed. No planned production boundary or milestone scope changed.
- Remaining risks: normalization can still create candidate false positives/negatives; literal placeholder authors such as `Unknown` remain ordinary stored values; Unicode case data can evolve with future runtimes; session defer state is intentionally not persisted; exact metadata groups do not catch author-subset records; large libraries retain O(record/title/author) normalized and presentation data; and interactive high-DPI/screen-reader behavior remains a manual follow-up.
