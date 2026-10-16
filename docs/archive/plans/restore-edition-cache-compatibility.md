# Restore edition cache compatibility

## Objective
Restore safe warm-cache reuse after the pseudonym query-policy version change.

## Scope
Existing edition proposal cache reads, current-rule revalidation, ISBN alias
backfill, and focused synthetic regression tests.

## Out of scope
Deleting the user's cache, disabling version checks, changing rate limits or
provider request budgets, real library mutation, or altering the active process.

## Relevant requirements
Cache reuse and bounded resources; complete version/input identities; no invented
evidence; current author-identity precision; corruption/expiry becomes a miss.

## Existing implementation inspected
FileEditionMetadataProposalCache, its tests, query identity construction, proposal
validation, identifier alias preparation, and the metadata-resolution use case.

## Proposed design
Probe the missing query-policy 1.4 identity for unchanged provider/query inputs.
Revalidate legacy positive candidates using the current proposal policy and keep
their original retrieval time. Continue refreshing legacy text misses and old
extraction failures. Permit valid 1.4 candidates to seed current ISBN aliases and
version the one-time backfill marker so an earlier incomplete pass cannot suppress it.
Never overwrite newer cached ISBN evidence with older backfill data.

## Files expected to change
Infrastructure cache and existing cache tests; functional requirements and this plan.

## Safety considerations
No real-library or active-process changes. Keep schema/provider/query identity,
expiry, reparse-point, size, and malformed-entry checks. Real-name-only candidates
rejected by current pen-name rules must not become reusable positives. No new
metadata, paths, credentials, provider payloads, or cache contents are logged.

## Implementation steps
1. Restore missing compatibility reads and current candidate validation.
2. Restore ISBN alias backfill from compatible prior entries.
3. Run focused and standard verification; document required restart and limitations.

## Tests
All 12 focused cache cases passed. They cover compatible legacy positive cold/warm
reads; both Open Library versions and another provider; rejection under current
pen-name rules; text miss refresh versus ISBN miss reuse; current ISBN alias
reuse; unchanged retrieval age; preservation of newer ISBN evidence; and existing
opaque-key, malformed-entry, expiry, and policy mismatch gates. Synthetic fixtures
make no HTTP requests and never use the real library or cache.

## Verification commands
All standard gates passed on 2026-10-16:

- `dotnet restore`
- `dotnet build --no-restore`
- `dotnet test --no-build --maxcpucount:1`: 1,053 passed, zero failed or skipped.
- `dotnet format --verify-no-changes`
- `git diff --check`
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

## Risks
Text misses cannot be blindly reused after changed author semantics. The already
running application retains the old implementation until restarted. Alias backfill
does bounded local disk work and must not create provider requests. This task did
not measure a live post-fix provider run; actual hit rates depend on query identity,
stored outcomes, expiry, and provider compatibility.

## Unresolved questions
None for this targeted fix.

## Progress
User approved the cache compatibility repair. Relevant documentation changes staged
before editing; cache and its tests had no prior changes. The missing 1.4 identities
are probed for recognized Open Library versions and unchanged other providers.
Legacy positives are resolved again under current pen-name/title/language/identifier
rules. Text misses remain refreshable, retrieval timestamps stay unchanged, and
ISBN backfill does not overwrite newer results. Alias marker identity was bumped
to 1.1 so a previous pass that ignored 1.4 entries cannot suppress corrected backfill.
Focused tests passed before standard verification. Production/test diffs, complete
cache identities, freshness, privacy, and resource bounds reviewed. No dependencies,
provider pacing, request budgets, matching thresholds, or mutation rules changed.

## Final outcome
Completed. Compatible older cached positives and ISBN aliases are reusable under
the current validation policy. Restart the updated application and prepare metadata
review again. No cache deletion or additional library Rescan is required solely for
this cache fix. Affected or uncertain legacy text misses still refresh online.