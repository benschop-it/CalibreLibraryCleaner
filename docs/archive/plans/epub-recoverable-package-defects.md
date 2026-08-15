# EPUB recoverable package defects

## Objective

Disqualify EPUB quality assessments only when the file cannot be safely opened or mandatory package facts cannot be recovered. Continue scoring readable publications with recoverable package and navigation defects.

## Scope

- Empty or missing manifest item content paths.
- Malformed optional EPUB 3 navigation and EPUB 2 NCX documents.
- Invalid Calibre-managed paths that cannot be safely resolved for inspection.
- EPUB inspection contracts, scoring findings, VersOne tolerances, focused tests, analyzer version, and governing documentation.

## Out of scope

- Relaxing archive traversal, containment, reparse-point, size, compression, encryption, file-identity, or network safeguards.
- Scoring files whose mandatory container or package document cannot be parsed.
- Repairing EPUB files or Calibre-managed paths.
- Changing EPUB scoring weights.

## Relevant requirements

- Inspect readability and package quality; malformed files become findings rather than crashes.
- Scores and recommendations remain explainable.
- Safety takes precedence over aggressive inspection.
- Analyzer behavior changes require an analyzer-version bump.

## Existing implementation inspected

- Every `EpubInspectionProblem` is currently disqualifying, regardless of recoverability.
- Preflight aborts on empty manifest hrefs and malformed navigation XML before readable-content analysis.
- VersOne.Epub 3.3.6 exposes focused options to skip invalid manifest items and malformed EPUB 2/3 navigation XML.
- Invalid managed paths are rejected before hashing and already produce `MANAGED_PATH_INVALID` library warnings, but are also synthesized into disqualified EPUB assessments without a verified file identity.

## Proposed design

Add a bounded recoverable-problem collection to `EpubInspectionResult`. The assessment engine emits these as warning findings with no direct score adjustment, while existing navigation, missing-resource, and text rules provide the quality penalties. Preflight records empty manifest paths and malformed navigation as recoverable, configures VersOne to skip them, and continues fact extraction. Invalid managed paths remain library warnings and are omitted from EPUB assessment targets because no verified file identity exists.

This plan originally left mandatory technical inspection failures disqualifying. The later `epub-unassessed-technical-failures.md` policy supersedes that status choice: only definitive file open/read failures remain disqualifying; other incomplete inspections are unassessed.

## Files expected to change

- `src/CalibreLibraryCleaner.Application/Assessments/EpubInspectionContracts.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/EpubAssessmentEngine.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Epub/VersOneEpubInspector.cs`
- focused Application and Infrastructure tests
- EPUB ADR and Milestone 4 documentation

## Safety considerations

- Unsafe archive and managed filesystem paths remain unopened.
- Invalid managed paths retain a library-level warning and cannot enter recommendations as assessed formats.
- Recoverable evidence is bounded by the existing finding evidence limit.
- VersOne downloading remains disabled with a fail-closed downloader.

## Implementation steps

1. Add regressions for completed assessments with recoverable package/navigation warnings and no assessment for invalid managed paths.
2. Add the recoverable-problem contract and warning mapping.
3. Continue preflight/fact extraction through the targeted defects and enable only the matching VersOne tolerances.
4. Bump the analyzer version and update documentation.
5. Run focused and full verification and review the complete diff.

## Tests

- Empty manifest href is skipped, reported as a warning, and does not suppress readable-content scoring.
- Malformed navigation XML is reported as a warning and the EPUB remains scored.
- Invalid managed path produces `MANAGED_PATH_INVALID` but no EPUB assessment row.
- Mandatory malformed container/package XML remains disqualifying.
- Archive and filesystem safety regressions remain green.

## Verification commands

```powershell
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~VersOneEpubInspectorTests"
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~EpubAssessmentTests|FullyQualifiedName~ScanLibraryUseCaseTests"
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
```

## Risks

- Some malformed navigation documents accepted by tolerant HTML parsing may expose incomplete navigation facts; the existing navigation penalty and warning make this visible.
- Future VersOne option behavior changes require analyzer revalidation.

## Unresolved questions

- None.

## Progress

- [x] Inspected controlling paths, contracts, safety boundaries, VersOne options, and existing tests.
- [x] Added failing recoverability regressions.
- [x] Implemented recoverable package defects.
- [x] Updated analyzer version and documentation.
- [x] Completed verification and diff review.

## Final outcome

Completed on 2026-08-01. Empty or missing manifest item paths, malformed optional navigation XML, and EPUB 2 NCX documents without a `navMap` are bounded recoverable problems. They produce zero-point `EPUB.PACKAGE` warning findings while readable-content assessment and normal scoring continue. Invalid Calibre-managed paths retain the library-level `MANAGED_PATH_INVALID` warning and do not produce an EPUB quality assessment because the scanner has no verified file identity. Mandatory container/package failures and all existing archive, identity, encryption, limit, extraction, and network safeguards remain unchanged.

The analyzer advanced to `epub-inspector/1.0.3` and the scoring model to `epub-quality/1.0.1`. Focused verification passed all 45 `VersOneEpubInspectorTests` and 36 relevant Application tests. The final solution build succeeded with zero warnings and errors. The full suite passed 535 tests with 2 expected opt-in real-Calibre tests skipped. `dotnet format --verify-no-changes`, `git diff --check`, diagnostics, complete diff review, and an independent read-only review all passed without findings.