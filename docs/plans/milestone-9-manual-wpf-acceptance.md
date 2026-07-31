# Milestone 9 Manual WPF Acceptance

## Objective

Complete the one remaining Milestone 9 acceptance item: a human WPF walkthrough
that verifies PDF assessment presentation, progress, cancellation, accessibility,
and analysis-only safety.

Use two complementary inputs:

1. generated, test-owned PDF fixtures for deterministic expected behavior; and
2. the disposable production-derived Calibre copy at
   `D:\dev\Private\Calibre Temp Library 1` for scale and real-world presentation.

The copied library is not synthetic and does not replace controlled fixtures.
It is explicitly disposable, but all repository analysis safety rules still
apply.

## Scope

- Launch the current WPF application on Windows from the verified Milestone 9
  commit or a later commit containing only reviewed documentation changes.
- Exercise Browse, Scan, PDF progress, Cancel, rescan, PDF list/detail,
  classification/confidence, sampling disclosure, score components,
  disqualification, severity filtering, keyboard navigation, and resizing.
- Use generated fixtures for known digital, scan-like, mixed, encrypted,
  malformed, metadata, identifier, outline, repeated-page, and large sampled
  cases.
- Use the copied library to observe bounded behavior over a realistic catalog
  and a heterogeneous PDF population.
- Capture local acceptance evidence outside the repository and outside every
  Calibre library.
- Compare the selected library before and after to prove the walkthrough did
  not change `metadata.db` or Calibre-managed files.
- Record the result in the Milestone 9 plan and handoff.

## Out of scope

- Cleanup-plan approval, cleanup execution, recovery, Calibre CLI invocation,
  or any other mutation workflow.
- Opening the copied library in Calibre while the walkthrough runs.
- Editing metadata, importing books, adding formats, deleting records, or
  using `.caltrash` as a recovery mechanism.
- Testing against the source library from which the disposable copy was made.
- Treating observed classifications in uncontrolled company-library files as
  deterministic ground truth.
- OCR, rendering, content comparison, Milestone 10 fingerprints, or retained-
  PDF selection.
- Committing manifests, screenshots containing book content, absolute library
  paths, or other acceptance evidence that may expose library data.

## Relevant requirements

- `AGENTS.md` and nested WPF/test instructions.
- `PLANS.md`.
- `docs/product-vision.md`.
- `docs/functional-requirements.md`.
- `docs/architecture.md`.
- `docs/domain-model.md`.
- `docs/quality-scoring.md`.
- `docs/safety-and-rollback.md`.
- `docs/test-strategy.md`.
- `docs/roadmap.md`.
- ADR 0001, ADR 0002, and ADR 0009.
- `docs/plans/milestone-9-pdf-assessment.md`.
- `docs/handoffs/milestone-9-handoff.md`.

Acceptance must preserve these invariants:

- `metadata.db` and all Calibre-managed files remain read-only.
- The source library is never selected.
- The PDF worker may be terminated because it is read-only; no Calibre mutation
  process is started or terminated.
- Cancellation publishes no partial replacement snapshot.
- PDF findings remain evidence only and cannot authorize cleanup, execution,
  or recovery.
- No extracted text, image bytes, links, attachment data, raw parser errors, or
  book content is retained in acceptance notes.

## Existing implementation inspected

Milestone 9 already has automated generated-fixture coverage across Domain,
Application, Infrastructure, Architecture, and WPF. The final automated
baseline is 503 passed, zero failed, and two skipped caller-gated real-Calibre
qualification tests.

`MainWindowViewModel` publishes PDF rows in one immutable batch after a
successful scan. `PdfAssessmentRowViewModel` and the PDF tab present status,
score components, classification, confidence, sample facts, encryption,
metadata, versions, and findings. `ScanLibraryUseCase` includes PDF assessment
for ordinary analysis but explicitly excludes it from execution and recovery
fresh scans.

The existing synthetic builders are test-local. If no prepared generated
Calibre fixture library is available at execution time, create one through a
separately reviewed test-only fixture-export mechanism outside every real or
copied library. Do not improvise by writing to the copied library's
`metadata.db` or managed folders.

The supplied copied root has the expected top-level `metadata.db` and managed
book directories. It also contains Calibre-owned `.calnotes` and `.caltrash`
directories, which must remain untouched. No database or book file was opened
during preparation of this plan.

## Proposed design

Run acceptance in four gates. A failure at any gate stops the walkthrough and
leaves the Milestone 9 checkbox open.

### Gate 1: controlled preflight

1. Confirm the repository is clean and `HEAD` contains Milestone 9 commit
   `9164482e2028da5207f82a81748029138639f7c0`.
2. Run the standard restore, build, test, and format baseline.
3. Confirm `CALIBRE_TEST_EXE` and `CALIBRE_TEST_ROOT` are unset. The
   caller-gated mutation qualification tests must remain disabled.
4. Close Calibre and every tool that may write either the source library or the
   copied library.
5. Confirm the WPF library selector will be given exactly
   `D:\dev\Private\Calibre Temp Library 1` and never the source path.
6. Create a timestamped evidence directory under the current user's temporary
   directory. Do not place evidence in the repository or a Calibre library.
7. Capture a local before-manifest containing relative path, file length,
   last-write UTC, attributes, and SHA-256 for every file in the copied
   library. Capture the `metadata.db` SHA-256 separately. Do not record file
   contents.
8. Record total file count and bytes so an incomplete after-manifest cannot be
   mistaken for equality.

### Gate 2: deterministic generated-fixture walkthrough

Use a generated Calibre fixture library outside the repository and outside the
production-derived copied library. The fixture matrix must contain, where
supported by the existing builders:

| Case | Expected WPF evidence |
| --- | --- |
| Digital text PDF | Completed result, digital-text evidence, score and both components. |
| Image-only scan-like PDF | Scan-like classification without a scan-quality penalty or OCR claim. |
| Scan-like PDF with text layer | `ScannedWithOcr` wording states inference only and confirms no OCR ran. |
| Mixed or illustrated PDF | `Mixed` or conservative `Unknown`; no claim that image-heavy means inferior. |
| Metadata/outline variants | Only documented metadata and outline evidence/components differ. |
| Valid and invalid ISBN | Valid checksum evidence is shown; invalid candidates are ignored without lookup. |
| Password-required or unsupported encryption | Disqualified, no score/components, explicit encryption reason. |
| Zero-byte, non-PDF, truncated, or malformed file | Controlled disqualification without raw exception or absolute path. |
| More than 200 pages | Exactly bounded sampling disclosure and no whole-document certainty. |
| Repeated/suspicious blank evidence | Bounded page examples and capped findings. |

For the generated fixture scan:

1. Verify Browse selects the intended fixture root and Scan starts once.
2. Confirm progress remains responsive through PDF assessment.
3. Confirm list rows appear only after successful completion.
4. Inspect each required row and compare it with the fixture's documented
   expected outcome.
5. Exercise every severity filter and verify source finding order is stable.
6. Verify keyboard traversal, accessible labels, focus order, selection changes,
   horizontal/vertical resizing, and at least one high-DPI scale.
7. Confirm the PDF tab exposes no keep, cleanup, execute, recovery, OCR, or
   content-opening action.

### Gate 3: copied-library cancellation and scale walkthrough

1. Select only `D:\dev\Private\Calibre Temp Library 1`.
2. Start a scan and cancel during PDF assessment when practical. Confirm the UI
   remains responsive, cancellation completes, no worker remains orphaned, and
   no partial new snapshot is published.
3. Start a fresh scan and allow it to complete. Record elapsed time, peak
   application/worker working set as an observation, PDF completed/disqualified
   counts, and whether any file-level timeout/resource result occurred. Do not
   record titles, metadata values, extracted text, identifiers, or absolute
   managed paths.
4. Inspect representative completed, disqualified, sampled, digital-text,
   image-heavy/scan-like, mixed/unknown, metadata-rich, and metadata-poor rows
   when those categories naturally exist. Absence of a category is not a
   failure because the copied data has no controlled expected composition.
5. Verify selection, details, filters, scrolling, keyboard use, progress, and
   resizing remain usable at realistic scale.
6. Do not enter Cleanup plan, Execution, or Recovery workflows. Do not approve
   or confirm any plan.

### Gate 4: non-mutation proof and closeout

1. Close the application normally after no scan is active.
2. Capture the same after-manifest and separate `metadata.db` SHA-256.
3. Require exact equality of relative paths, lengths, hashes, timestamps, and
   attributes. Any difference is a failed safety acceptance until explained;
   do not normalize or repair the copied library.
4. Confirm the repository working tree is unchanged by the walkthrough.
5. Record a concise pass/fail table and environment versions in the evidence
   directory. Screenshots must avoid or redact book content and are never
   committed.
6. If every required controlled-fixture and safety check passes, update the
   Milestone 9 plan checkbox and final outcome, then update the handoff with the
   date, environment, copied-library role, observations, and manifest result.
7. If any check fails, leave Milestone 9 acceptance open and record the smallest
   reproducible synthetic case. Do not work around a failure by weakening the
   expected result.

## Files expected to change

Planning changes:

- `docs/plans/milestone-9-manual-wpf-acceptance.md`.
- `docs/plans/milestone-9-pdf-assessment.md`.
- `docs/handoffs/milestone-9-handoff.md`.

Acceptance closeout changes after the walkthrough:

- `docs/plans/milestone-9-pdf-assessment.md`.
- `docs/handoffs/milestone-9-handoff.md`.

No production or test code change is authorized by this plan. If a reusable
test-only fixture export is required, update this plan and review that separate
change before implementation.

## Safety considerations

- “Disposable” reduces recovery impact; it does not permit mutation during an
  analysis acceptance.
- The copied library may contain private or licensed data. Keep all evidence
  local, bounded, content-free, and outside source control.
- Full recursive hashing may be expensive, but it is the strongest cheap proof
  that analysis did not alter managed files. Do not substitute timestamps alone.
- Calibre, synchronization clients, antivirus remediation, indexing tools, or
  another writer can invalidate the manifest comparison. Stop and repeat from
  a quiet copy rather than attributing unexplained changes to the application.
- A classification observed in uncontrolled data is not correctness ground
  truth. Only documented generated fixtures can satisfy deterministic cases.
- Worker isolation is defense in depth, not a complete hostile-code sandbox.

## Implementation steps

1. Review and approve this acceptance plan.
2. Prepare or identify the generated fixture library and its expected-result
   matrix without modifying the copied library.
3. Run Gate 1 and retain local evidence.
4. Run Gate 2 against generated fixtures.
5. Run Gate 3 against the disposable copied library.
6. Run Gate 4 and compare manifests.
7. Record failures or close the Milestone 9 manual acceptance item.
8. Only after closeout, begin a separate Milestone 10 execution plan.

## Tests

The walkthrough does not replace automated tests. Before and after any code fix
caused by a failed scenario, run the focused affected suite and the full
baseline. The acceptance evidence must cite the existing generated-fixture
tests covering each controlled case.

Required manual assertions are the Gate 2 fixture matrix, Gate 3 cancellation
and scale behavior, accessibility checks, and Gate 4 exact non-mutation proof.

## Verification commands

Run before the walkthrough:

```powershell
dotnet --info
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git status --short --branch
```

Launch the already-built WPF application without restoring or rebuilding:

```powershell
dotnet run --project src/CalibreLibraryCleaner.Wpf/CalibreLibraryCleaner.Wpf.csproj --no-build
```

Run after the walkthrough:

```powershell
git status --short --branch
```

The before/after manifest commands must be reviewed immediately before use so
their output path is outside the library and their traversal excludes no
Calibre-managed file. They must not use write APIs against the selected root.

## Risks

- A full copied-library scan may take substantial time because every eligible
  file is hashed and each PDF receives a fresh worker with a 60-second hard wall
  limit.
- The copied library may contain malformed or adversarial inputs that expose a
  parser/runtime defect before normal quota callbacks. Process containment
  reduces but does not eliminate that risk.
- Test-local fixture builders do not currently guarantee a reusable manual
  Calibre fixture library. This is the principal preparation dependency.
- Large local manifests can expose library names and paths. Keep them temporary
  and never commit them.
- External writers can create false non-mutation failures.
- The WPF walkthrough remains a human observation and cannot replace automated
  accessibility tooling or deterministic tests.

## Unresolved questions

- Is a reusable generated Milestone 9 Calibre fixture library already available
  outside source control? If not, the test-only export mechanism needs separate
  review before Gate 2.
- Which display scaling values and assistive technology are available on the
  acceptance machine? At minimum test 100% and one high-DPI setting.
- How long is an acceptable full copied-library scan on this hardware? Record
  the observation; do not turn it into a universal performance threshold during
  acceptance.

## Progress

- [x] Confirm the supplied disposable copy has a top-level `metadata.db` and
  managed library directories without opening the database or book files.
- [x] Define deterministic-fixture, copied-library, cancellation, UI, safety,
  and closeout gates.
- [ ] Approve this plan.
- [ ] Prepare or identify the generated fixture library.
- [ ] Complete controlled generated-fixture walkthrough.
- [ ] Complete copied-library cancellation and scale walkthrough.
- [ ] Verify exact before/after copied-library manifests.
- [ ] Record results and close the Milestone 9 manual acceptance item.

## Final outcome

Planning is complete. No walkthrough has been performed and no Calibre file or
database has been opened or modified by this planning task. Milestone 9 manual
acceptance remains open until all four gates pass and the result is recorded in
the Milestone 9 plan and handoff.