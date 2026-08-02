# EPUB doctype compatibility

## Objective

Permit bounded EPUB XML documents to contain document type declarations without allowing DTD grammar processing, entity expansion, external resolution, or network access.

## Scope

- EPUB container, package, navigation/NCX, and encryption XML parsed by the bounded infrastructure adapter.
- Focused regressions for harmless doctypes and entity-bearing XML.
- EPUB analyzer version and governing Milestone 4 documentation.

## Out of scope

- Resolving or validating external DTDs.
- Expanding entities declared by internal or external DTD subsets.
- Changing EPUB quality weights or scoring formulas.
- Changing the VersOne dependency.

## Relevant requirements

- EPUB files are untrusted and must be inspected without network access or unbounded allocation.
- Malformed files become findings rather than application crashes.
- Analyzer behavior changes require an analyzer-version bump.

## Existing implementation inspected

- `VersOneEpubInspector` rejects any encoded `<!DOCTYPE` marker before parsing and configures its own XML reader with `DtdProcessing.Prohibit` and `XmlResolver = null`.
- VersOne.Epub 3.3.6 internally parses XML with `DtdProcessing.Ignore`; it does not expose XML-reader settings beyond header handling.
- The assessment engine converts inspection problems into disqualifying findings and suppresses the numeric score.
- EPUB 3.3 permits constrained document type declarations but prohibits external entity declarations in internal subsets.

## Proposed design

Remove the blanket doctype byte scan. Parse bounded XML with `DtdProcessing.Ignore` and `XmlResolver = null`, so declarations are accepted but their grammars are not processed. References to entities declared only by a DTD remain undeclared and produce a structured malformed-package problem. Keep all existing byte, character, archive, cancellation, and no-network limits.

## Files expected to change

- `src/CalibreLibraryCleaner.Infrastructure/Epub/VersOneEpubInspector.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/EpubAssessmentEngine.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Epub/VersOneEpubInspectorTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/EpubAssessmentTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recommendations/ConsolidationRecommendationPolicyTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/EpubAssessmentRowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`
- `docs/adr/0004-select-versone-epub-inspection-stack.md`
- `docs/plans/milestone-4-epub-assessment.md`

## Safety considerations

- DTD processing remains disabled by using `Ignore`, not `Parse`.
- The resolver remains explicitly null in application-owned XML parsing.
- VersOne 3.3.6 also uses `DtdProcessing.Ignore`; content downloading remains disabled with a fail-closed downloader.
- Entity references and malformed declarations must remain controlled failures with redacted explanations.

## Implementation steps

1. Add regressions that accept harmless doctypes and reject entity use.
2. Remove blanket declaration scanning and ignore DTD grammars during bounded XML parsing.
3. Bump the analyzer version and update documentation.
4. Run focused tests, build, full tests, formatting, and review the diff.

## Tests

- A navigation XHTML document with `<!DOCTYPE html>` completes assessment.
- A DTD-declared external entity reference remains a structured package-malformed problem and does not expose its URI.
- An external reference whose host contains a Unicode line separator remains assessable and produces the canonical host after removing the separator.
- Existing EPUB safety and scoring tests remain green.

## Verification commands

```powershell
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~VersOneEpubInspectorTests"
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~EpubAssessmentTests"
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

## Risks

- A future VersOne release could change its internal DTD behavior; dependency upgrades must revalidate these safety tests.
- Ignored DTDs cannot provide entity values required by the document, so those documents remain unassessable.

## Unresolved questions

- None.

## Progress

- [x] Inspected the local parser path, tests, requirements, accepted ADR, and VersOne 3.3.6 source.
- [x] Added failing compatibility regression.
- [x] Implemented bounded doctype tolerance.
- [x] Updated analyzer version and documentation.
- [x] Completed verification and diff review.

## Final outcome

Completed on 2026-08-01. The analyzer now accepts document type declarations by ignoring their DTD grammars while retaining `XmlResolver = null`, bounded reads, disabled content downloading, and the fail-closed downloader. An EPUB 2 NCX external doctype and an HTML5 doctype complete assessment without network access; an external entity reference remains a redacted structured malformed-package failure. External URI evidence removes Unicode line separators before canonicalizing hosts, while other invalid IDN hosts degrade to bounded `host:invalid` evidence. The analyzer advanced to `epub-inspector/1.0.2`; `epub-quality/1.0.0` is unchanged.

Focused verification passed: 45 `VersOneEpubInspectorTests` and 19 `EpubAssessmentTests`. The full solution build succeeded with zero warnings and errors. The full suite passed 534 tests with 2 expected opt-in real-Calibre tests skipped. `dotnet format --verify-no-changes` and `git diff --check` succeeded. The first full-build attempt was blocked by the running WPF process; after the user closed it, the standard output build succeeded without an alternate artifact path.