# Product Vision

## Problem

Large Calibre libraries often contain duplicate records, conflicting versions, incorrect metadata, missing covers, malformed files, and uncertainty about which format to keep. Calibre can merge records, but safe decisions are labor-intensive at scale.

## Vision

Provide a local-first, safe, explainable workflow that finds duplicates, assesses format quality, recommends a consolidation result, requires approval, backs up affected data, executes through supported Calibre mechanisms, verifies results, and retains audit and rollback information.

## Principles

- Safety over aggressive automation.
- Every score and recommendation is explainable.
- Automatic action only for very high-confidence cases.
- Reversibility for every destructive operation.
- No direct database mutation.
- Core analysis works without cloud services.
- AI is optional and advisory.

## Success criteria

- Substantially reduce repetitive review work.
- Never silently discard a unique format.
- Clearly explain why one version is preferred.
- Scale to tens of thousands of records.
