# Lot C — Discord roster reads, links and interface

Contract: [shared contracts](2026-09-30-discord-roster-contracts.md).

| Tasks | Detailed plan | Deliverable |
|---|---|---|
| C1–C4 | [Read API](parts/lot-c-s1-api-read.md) | Ranks, tokenization, reconciliation, guild/member/event/sync reads, cross profile |
| C5–C7 | [Links and retention](parts/lot-c-s2-api-links.md) | Suggestions and validation, guild mapping/rank configuration, retention |
| C8–C10 | [Web](parts/lot-c-s3-web.md) | Navigation, guild pages and tabs, multi-memberships, citizen/org sections, smoke tests |

This lot follows A and can run in parallel with B integration. Preserve the existing
bot-backed Discord profile enrichment. The `(public)` page group is still protected
by the private-site middleware; new pages must keep server-only API calls and validation.

All C1–C10 tasks are implemented. Actual validation results and runtime limitations
are recorded in the implementation progress file.
