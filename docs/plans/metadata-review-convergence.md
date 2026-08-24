# Metadata review convergence

## Objective

Prevent already-applied provider metadata from returning as selected work after a
rescan or repeated metadata preparation.

## Scope

- Compare every writable provider field with the current retained record.
- Mark proposals with no remaining changes as already applied and non-actionable.
- Schedule only fields that still differ.
- Preserve unavailable provider outcomes and cache behavior.

## Out of scope

- Changing provider matching confidence.
- Persisting provider metadata inside Calibre.
- Replacing an existing cover when a cover is already present.

## Relevant requirements

The workflow is a one-time cleanup and repeated preparation must converge instead of
reselecting the same updates.

## Existing implementation inspected

Review reconciliation currently defaults every Medium/High proposal to Apply in each
new generation. Execution schedules every populated candidate field even when it
already equals the current Calibre value.

## Proposed design

Use one deterministic Domain policy for field applicability. Application reconciliation,
WPF status/filtering, and execution planning consume the same result.

## Files expected to change

- Domain metadata applicability and decision models.
- Application review preparation and metadata operation planning.
- WPF review status/filtering.
- Focused Domain, Application, and WPF tests.

## Safety considerations

Comparison may suppress only writes whose resulting supported value is already present.
Existing covers satisfy provider cover availability because Calibre does not retain
provider cover provenance.

## Implementation steps

1. Implement and test field-level applicability.
2. Disable already-applied subjects during reconciliation.
3. Schedule only applicable fields.
4. Expose Already applied status/filter and summary.
5. Run focused and standard verification.

## Tests

- Fully equal candidate has zero applicable fields.
- Partially equal candidate schedules only changed fields.
- Already-applied Medium/Low proposal is unchecked and disabled.
- Repeated metadata execution creates no duplicate writes.

## Verification commands

```powershell
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
```

## Risks

- Calibre normalization must be mirrored for authors, identifiers, languages, and dates.
- Existing cover bytes cannot be compared to provider cover identity after rescan.

## Unresolved questions

- None for this slice.

## Progress

- [x] Current review and execution behavior inspected.
- [x] Field applicability policy implemented.
- [x] Review and execution convergence wired.
- [x] Verification passed: 841 tests, formatting, diagnostics, and diff check.

## Final outcome

Implementation and automated verification complete. Package and live repeated-review
acceptance remain.