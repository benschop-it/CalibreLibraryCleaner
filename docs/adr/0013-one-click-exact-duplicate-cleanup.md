# ADR 0013: Use One-Click Exact Duplicate Cleanup

- Status: Accepted
- Date: 2026-08-02
- Amends: ADR 0011, ADR 0012

## Context

Exposing cleanup-plan generation, validation, approval, backup-folder selection, preparation, and acknowledgement checkboxes made exact duplicate cleanup unusable. Those controls represent internal implementation stages rather than the user's goal.

Users need to review generated keepers, optionally override them, and remove all exact duplicates in one operation.

## Decision

The Exact file duplicates tab provides:

1. one generated keeper per group using the documented retention policy;
2. row selection to override that keeper;
3. one `Remove duplicates` command covering all eligible groups.

The button click authorizes the operation. No cleanup-plan, approval, backup-folder, preparation, or acknowledgement controls are shown.

The bulk workflow uses only typed Calibre commands. It first stages complementary source formats in application-local temporary storage and verifies their fingerprints. It then:

1. adds complementary formats to an unambiguous target record;
2. removes non-retained exact format copies and transferred source formats;
3. removes records that become empty.

A record selected as keeper in any group is never merged away. A non-keeper source is merged only when all remaining formats map to one target and no non-identical same-format conflict exists. Ambiguous or conflicting records remain unchanged and are reported in the result.

Successful commands durably update projected state as defined by ADR 0012. Failed or ambiguous commands stop the workflow and mark state uncertain. The user's existing library backup is outside this per-run workflow; unique formats are removed only after a verified staged copy has been added to the target.

Technical cleanup-plan, cleanup-execution, and recovery tabs are hidden from the normal interface.

## Consequences

- The primary workflow matches the product goal and scales across all exact groups.
- Internal plan infrastructure may remain for diagnostics and historical compatibility but is not user-operated.
- Some records can remain when automatic merging would overwrite conflicting content or has multiple possible targets.
- Temporary transfer staging is automatic and deleted after execution.

## Rejected alternatives

- Requiring users to create and approve one plan per group.
- Requiring a per-run backup destination or acknowledgement checkboxes.
- Overwriting non-identical same-format files.
- Choosing a global target that silently ignores per-group keeper overrides.