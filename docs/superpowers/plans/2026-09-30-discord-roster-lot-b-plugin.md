# Lot B — ScTracker Vencord plugin

Contract: [shared contracts](2026-09-30-discord-roster-contracts.md).
Libraries: [B1–B5 detailed plan](parts/lot-b-s1-lib.md).
Review: [resume analysis](../analyses/2026-09-30-vencord-reprise.md).

## B1–B5 — pure libraries

Implement the package, installer, fingerprint and pinned transport, minimal payload,
coverage, native arguments, pacing, result messages, search cursor and chunk tracking.
Apply the contract's review corrections in addition to the original code examples:
stop reason for coverage, nonce filtering, collection deadline, transport deadlines
and response bound, and cleanup of timers/listeners.

These libraries do not provide an activatable Discord plugin on their own. Keep tests
outside `scTracker.desktop/`, and keep Node value imports out of the renderer graph.

## Implemented integration tasks

| Task | Files / behavior | Acceptance criteria |
|---|---|---|
| B6 | Discord response adapter and `collect/{memberSearch,refresh,roleMembers,strategy}.ts` | Validate actual REST/Flux shapes, nonce propagation, paging above 1,000 members, shared budget across fallbacks, 202/403/429, stale chunks, timeouts, partial exits |
| B7 | `native.ts` | Validate unknown IPC arguments, return local refusals, construct the fixed path, send only after pinning; test no connection on refusal and no bytes on wrong pin |
| B8 | `settings.tsx` and DataStore key access | API key stays outside settings/Cloud Sync; guild tracking and last status persist; storage failures are visible |
| B9 | `index.tsx`, menus and orchestration | One active collection, manual triggers only, sequential guilds with pauses, batch stop/continue rules, progress, cancellation checks before IPC, cleanup on stop |
| B10 | CI, installation and README | Build and `testTsc` in the pinned Vencord tree with its own manager; document copy/build/inject/restart; perform the real Discord checklist |

B6 includes runtime protocol guards and tests of supported REST/Flux envelopes. The
declaration files use `any` for important endpoints, so successful typechecking does
not validate a running Discord client. Refresh requires a matching nonce; incompatible
envelopes cause a visible refusal. The real Discord acceptance checklist remains to run.

The collection deadline is shared across all strategies, waits and retries. Stop with
a partial snapshot before the API's 30-minute bound. If a non-cancellable request returns
too late, do not falsify duration or upload a known-invalid payload.

Before native IPC, verify cancellation again after all awaited work, including DataStore.
An upload already in progress uses a separate UI state; it cannot promise to undo a server write.

## Validation and delivery

Run the local unit suite and typecheck with the package manager pinned by `vencord/package.json`.
The upstream checkout uses its own `packageManager` (pnpm 11.9 at the chosen reference),
preferably Node 24 to match its CI. End-to-end uploads require lot A; the `/discord` link
and the full user journey use the implemented lots A and C. No Discord injection or
production deployment has been performed during this implementation.
