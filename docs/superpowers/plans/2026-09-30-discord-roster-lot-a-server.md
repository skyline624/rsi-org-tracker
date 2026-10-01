# Lot A — Discord roster server foundation

Contract: [shared contracts](2026-09-30-discord-roster-contracts.md).
Resume analysis: [review and corrections](../analyses/2026-09-30-vencord-reprise.md).

## Implementation order

| Tasks | Plan | Deliverable |
|---|---|---|
| A1–A4 | [Authentication, errors and budgets](parts/lot-a-s1-auth.md) | Scoped expiring keys, dedicated authentication, ProblemDetails codes, ingest rate limiter |
| A5–A8 | [Data and diff](parts/lot-a-s2-data.md) | Additive schema, snapshot diff, bounded writes, erasure |
| A9–A12 | [Ingest API](parts/lot-a-s3-ingest.md) | Validation, shared write gate, ingestion, guard and administrator routes |
| A13–A14 | [Settings and operations](parts/lot-a-s4-web-ops.md) | Key/configuration panel, nginx route, deployment documentation |

All A1–A14 tasks are implemented. Legacy schema adoption was also corrected:
only migrations whose target tables and columns already exist may be baselined.
New additive migrations must actually run on databases without an EF history.

The ingestion endpoint and nginx configuration are implemented. No production route
was opened; deployment follows the documented collector-first migration procedure.

## Validation

Run the API and collector suites, then check EF model/migration consistency for both
contexts. Migration tests must retain legacy full keys and exercise adoption without
history as well as a normal upgrade. Authorization tests enumerate all private routes
and refuse ingest credentials outside the ingestion route.

Use the progress file for actual execution results; unchecked steps and expected test
counts in the detailed plans remain planning examples, not proof of completion.
