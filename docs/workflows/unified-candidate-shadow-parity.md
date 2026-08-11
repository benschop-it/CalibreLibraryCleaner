# Unified Candidate Shadow Parity

## Scope

Before retiring the legacy standalone Metadata, standalone Expanded, and
`Cleanup all` mutation paths, six synthetic shadow comparisons ran the legacy
pure planners and the unified Candidate planner against the same snapshots and
review selections.

The comparisons ran on 2026-08-10 and passed before legacy deletion.

## Equivalent behavior

| Fixture | Shadow result |
| --- | --- |
| Metadata-only, non-identical same-format alternatives | Membership, selected keeper, format removal, and empty-record removal match. The keeper's same-format file wins. |
| Expanded group with complementary formats | Membership, generated keeper, transfer source/target/format, format removals, and record removals match. |
| Advisory `To be reviewed` Expanded group | Both planners execute the group unless the user selects Skip. |
| Missing physical format facts | Both planners skip the complete group before worker startup. |

Operation comparison ignores legacy category prefixes. Unified operation IDs are
intentionally category-neutral and deterministic.

## Intentional differences

1. **Conflicting complementary Metadata sources**

   Legacy standalone Metadata cleanup skipped a group when multiple non-identical
   sources supplied a format and no recommendation selected one. Unified Candidate
   cleanup uses the documented deterministic candidate quality ranking, then record
   ID, to select one source. The unified planner transfers that source and removes
   every non-keeper record. This is intentional because candidate analysis now
   computes one keeper-authoritative group with current assessment evidence.

2. **Projected keeper placeholders**

   Legacy Metadata cleanup allowed `ProjectedPresent` formats on the keeper after
   standalone Exact cleanup. Unified Candidate cleanup skips any group containing
   a non-physical association. This is intentional: trusted post-Exact refresh must
   rebind every executable association as physical `Present` with a current path,
   fingerprint, and observation before Candidate analysis.

## Safety conclusion

The unified planner preserves accepted keeper-authoritative transfer/removal
semantics where the legacy workflows were compatible. Its intentional differences
are stricter physical-state validation and deterministic resolution using unified
candidate quality evidence. Exact cleanup remains a separate unchanged workflow.
