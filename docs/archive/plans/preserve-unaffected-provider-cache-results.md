# Preserve unaffected provider cache results

## Objective
Avoid repeating ordinary provider misses after a local matching normalization
change, without weakening provider validation or local pseudonym matching.

## Scope
Edition query construction/validation, compatible proposal-cache reads, regression
coverage and measured offline warm/cold network work.

## Out of scope
Changing rate limits, bypassing validation, deleting live caches, direct library
mutation, new workflow/UI stages, or stopping/modifying the running application.

## Relevant requirements
Same-work precision, cache reuse, complete input/algorithm version identities,
provider-neutral evidence, bounded resources, and unchanged mutation authority.

## Existing implementation inspected
CandidateMetadataNormalizer, EditionMetadataLookupQuery/ProposalPolicy,
OpenLibraryBibliographicProvider fallback work validation, FileEditionMetadataProposalCache,
existing cache tests and runtime aggregate cache/progress evidence.

## Proposed design
Restore literal provider-edition author comparison semantics independently of local
pseudonym-aware matching. Clean explicit annotations only in outgoing author query
values, so affected queries receive different complete input identities. Query
policy 1.6 identifies the correction. Reuse 1.4 negative results only for unchanged
provider versions and exact complete query inputs; refresh historical extraction
misses from older provider versions and uncertain 1.5 text misses. Continue current
candidate revalidation and original retrieval timestamps. Preserve the original
one-argument AuthorKeys API and expose explicit provider comparison separately.

## Files expected to change
Domain author-key/query policy and tests; edition adapter fallback compatibility;
Infrastructure proposal cache and existing tests, including orchestration/cache
integration coverage; functional/architecture documentation and this plan.

## Safety considerations
Local matching and final-author Apply behavior remain unchanged. No global cache
version rollback or blind reuse across changed inputs/provider extraction versions.
No real library writes or ordinary metadata logging. Existing expiry, corruption,
path, provider identity and mutation checks remain in force.

## Implementation steps
1. Separate edition comparison and normalize only affected outgoing query values.
2. Restore precisely compatible negative cache reads and verify offline reuse.
3. Run standard verification, document real measurements and remaining limits.

## Tests
Focused validation passed: 46 Domain and 24 cache/provider cases. Coverage includes
literal provider validation with unchanged local pseudonym matching; normalized
outgoing annotation inputs with immutable source names; compatible 1.4 negative
reuse for both providers; rejection of older extraction versions, uncertain 1.5
text misses, and changed annotation inputs; expiry and malformed-state boundaries.
The offline 20-book orchestration fixture measured 20 provider calls when uncached,
zero calls and 20 hits on first compatible-cache preparation, and zero calls and
20 hits on its warm repeat. No HTTP, personal library, or real cache is used by tests.

## Verification commands
All standard gates passed on 2026-10-16:

- `dotnet restore`
- `dotnet build --no-restore`
- `dotnet test --no-build --maxcpucount:1`: 1,076 passed, zero failed or skipped in the final combined suite.
- `dotnet format --verify-no-changes`
- `git diff --check`
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

The unchanged local calibration/holdout matching corpus also passed.

## Risks
Incorrect compatibility inference could suppress newly discoverable editions.
Compatibility is restricted to unchanged validation and exact input/provider
identity. Changed catalog metadata, expired failures and uncached books still need
requests. Google Books retains its three-second pacing for genuine requests.
One-time ISBN backfill can perform local disk work. Live post-fix preparation
latency remains to be measured after restarting the next verified build.

## Unresolved questions
None for the approved provider/local matching separation.

## Progress
User approved the broader correction. Relevant current changes staged before
editing. The cache audit found 31,298 policy-1.4 misses from the current Open Library
adapter versus 4,212 positive entries; restoring only positives was insufficient.
The active process was confirmed to be in paced Google Books lookup; old Open
Library log lines preceded that process and were not evidence of the current run.
The provider/local identity boundary is implemented. Explicit edition validation
and adapter fallback use pre-pseudonym literal-key behavior. Only outgoing
annotated author values are cleaned. Query policy is 1.6; compatible 1.4 negative
reads require the exact provider version and complete input identity. Legacy
positives and ISBN aliases retain current validation, timestamps and freshness.
The first compile check caught method-group compatibility after adding an optional
parameter; an explicit overload preserved the original API and focused checks passed.
The corrected binary later showed 9 hits/129 requests at group 50. A direct identity
audit proved this was deterministic ordering, not failed cache reads: only 2 of the
first 50 groups had pre-run 1.4 keys. Across all 17,218 current groups, 11,652 are
wholly cached NotFound sequences, 1,524 have terminal cached results, and 4,042
genuinely require online work; 30,436 cached variants are usable before their
group's first miss. A global cache prepass now completes those 13,176 fully cached
groups before any provider call while preserving variant precedence and all eventual
online work. The first prepass test exposed a nullable comparison that treated null
as cache-complete and suppressed provider calls. Requiring a concrete proposal fixed
it; all 12 orchestration tests then passed, including fallback, cooldown, request
limits, provider independence, and cached-before-online behavior. Standard gates
must be rerun after this adjacent change.

## Final outcome
Completed and verified. Ordinary compatible provider misses remain reusable; only
affected query identities refresh. A global prepass makes cache-first progress visible
before online work without changing request budgets, fallback order, or final results.