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

The button starts a per-run confirmation that a complete external library backup exists. No cleanup-plan, backup-folder, or preparation controls are shown, and the application does not create or verify the external backup.

The bulk workflow uses only the typed persistent worker. It then:

1. adds complementary formats to an unambiguous target record;
2. removes non-retained exact format copies and transferred source formats;
3. removes records that become empty.

A record selected as keeper in any group is never merged away. A non-keeper source is merged only when all remaining formats map to one target and no non-identical same-format conflict exists. Ambiguous or conflicting records remain unchanged and are reported in the result.

Complete successful worker chunks durably update projected state as defined by ADR 0012. Failed or ambiguous chunks are logged, stop the workflow, and mark state uncertain. The user's existing library backup is outside this workflow.

Technical cleanup-plan, cleanup-execution, and recovery tabs are hidden from the normal interface.

## Consequences

- The primary workflow matches the product goal and scales across all exact groups.
- General plan, backup, execution-history, and recovery infrastructure is removed.
- Some records can remain when automatic merging would overwrite conflicting content or has multiple possible targets.
- Complementary transfers are fingerprint-verified by the persistent worker.

## Rejected alternatives

- Requiring users to create and approve one plan per group.
- Requiring a per-run backup destination or acknowledgement checkboxes.
- Overwriting non-identical same-format files.
- Choosing a global target that silently ignores per-group keeper overrides.