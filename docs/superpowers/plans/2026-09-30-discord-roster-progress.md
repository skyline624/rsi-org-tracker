# Discord rosters — implementation progress

Updated: 2026-10-01, after the requested Discord interface changes, Config-tab navigation fix and selectable organization search. Branch: `lot-14-discord-roster`.

## Status

| Work | Status |
|---|---|
| A1–A4 — scoped expiring keys, dedicated ingest authentication, ProblemDetails codes, ingest rate limit | Implemented and tested |
| A5–A8 — additive schema, pure diff, bounded resumable writes, erasure | Implemented and tested |
| A9–A12 — validation, write gate, ingestion endpoint, mass-departure signal, direct administrator routes | Implemented and tested |
| A13–A14 — ingest configuration, settings key panel, nginx location, deployment docs | Implemented and tested; nginx not applied on the server |
| Legacy schema adoption | Baselines only migrations whose target tables and columns already exist; tested |
| B1–B5 — plugin package, pure libraries, installer | Implemented and tested |
| B6–B10 — collection strategies, native bridge, settings, orchestration, CI job, README | Implemented, unit-tested, built and type-checked in the pinned Vencord tree; first real Discord member-search sync accepted locally on 2026-10-01; gateway fallbacks and large-guild acceptance remain |
| C1–C10 — reads, reconciliation, suggestions and links, guild configuration, retention, web pages | Implemented and tested |
| Commits | None yet: all changes are local and uncommitted. Nothing was pushed. |

## Validation — local runs (2026-09-30 and 2026-10-01)

The .NET, web unit/build, plugin and Discord browser checks were rerun after the departure
policy and interface changes on 2026-10-01. Release publication, Linux standalone startup,
anonymous browser smoke, deployment scripts and nginx checks below were completed earlier
in this local session; those packaging and deployment paths were unchanged by this update.

| Check | Result |
|---|---|
| NuGet restore, `--locked-mode` | OK |
| `dotnet build Collector.sln -c Release` | OK, 0 errors; clean compilation reports the pre-existing EF1002 warnings in `OrganizationMemberRepository` and CA1416 in `DiscordTokenStore` |
| Collector tests | **406 / 406** passed |
| API tests | **366 / 366** passed |
| EF `has-pending-model-changes`, TrackerDbContext and ApiDbContext | No pending changes (the API design-time JWT warning is expected) |
| Release publish, API and Collector | OK; each output has exactly its own source `appsettings.json`, and only Collector carries `migrate-mode.txt` |
| Web `tsc --noEmit` | OK |
| Web vitest | **423 tests / 28 files** passed |
| Web `next build` | OK; `/discord`, `/discord/[guildId]` and `/discord/multi` built |
| Web Linux standalone build and startup | OK in a disposable official Node 22 Debian container, frozen `pnpm@10.27.0` install; standalone server returns 200 for `/login` |
| Playwright Discord acceptance (`playwright.discord.config.ts`, loopback API fixture) | **8 / 8** passed after the selectable organization search, with `E2E_BROWSER_CHANNEL=chrome` |
| Playwright anonymous smoke on `next start` | **12 / 12** passed, including `/discord` and `/discord/multi` redirects |
| Plugin `tsc --noEmit` | OK |
| Plugin vitest | **290 tests / 18 files** passed |
| Vencord `7f0c10c` with the plugin installed: `testTsc`, then `build` | OK; the renderer contains `ScTracker` and the main-process bundle carries its native `postSync` |
| Deployment shell syntax and local script checks | OK in a disposable Ubuntu container: rollback, collector unit and development guard checks |
| `nginx -t` | OK in a disposable nginx container with the repository configuration and test certificate; no remote configuration applied |

**Fix made during the earlier review.**
`DiscordRosterRepositoryTests.Snapshot_HoldsTheServer_AllItsRolesAndMembers_AndThePayloadsAccounts`
still expected the original three-field `AccountSnapshot`. The record now also carries `IsBot`
and `LastSeenAt`, which the diff uses to never apply names older than the stored ones. The
repository loads them correctly, so only the test's expectation was updated.

## Requested policy and interface change — 2026-10-01

The tracker now records every departure established by a valid complete observation. A loss
of at least 10 active members and more than 25% sets `MassDepartureDetected` and displays
“DÉPARTS MASSIFS”; it never downgrades the sync or requires approval. Partial syncs still
produce no departures. Integration tests verify the recorded events, duplicate resubmission,
6,000 automatic departures within the 5,000-row transaction budget, and legacy interrupted
receipts. The plugin explains that all mass departures were recorded.

All four web controls and their server actions were removed: manual departure approval,
guild reset, guild deletion/exclusion and member deletion/exclusion. The manual approval API
route was removed too; direct admin erasure APIs remain outside the web interface. Browser
acceptance tests verify absence of the controls even for an administrator.

No SQLite migration is needed. `MassDepartureDetected` maps to the existing
`DepartureGuardTripped` column; the old `AllowMassDepartureOnce` column is unused. Ingestion
responses keep `departureGuardTripped: false` for already installed plugins. Existing data
and legacy pending receipts remain readable.

The local database was backed up before rebuilding. The API and website were restarted
using the existing accounts, keys and certificate; the HTTPS proxy was preserved. Actual
authenticated Liberastra pages return 200 without any removed control, and the removed
approval route returns 404. The real roster remains **168 members, 30 roles, one complete
baseline**, with SQLite integrity and foreign keys checked. Updated Vencord bundles were
installed and their hash verified without interrupting Discord; its current renderer will
load the new informational text on the next reload.

## Config-tab navigation fix — 2026-10-01

The user reported that clicking Config did not change the URL. Direct page loading rendered
the real Liberastra configuration, but an acceptance test clicking through History, Gaps and
Suggestions reproduced a stalled transition to Config. Disabling link prefetch did not reliably
fix it. The guild tab bar and the unmapped-server configuration notice now use ordinary page
links, so each click loads the selected server-rendered view without the stalled client transition.

Browser acceptance now checks actual tab clicks, configuration updates after navigating from
Suggestions, and both ways to open Config on an unmapped guild with no RSI ranks. All six
Discord browser tests, the production build and all 423 web unit tests pass. Three successive
runs of each navigation/configuration scenario also pass (nine checks). The local website
was restarted independently of the API and HTTPS ingest proxy. Read-only SQLite verification
still finds 168 memberships, 30 roles, two syncs, integrity `ok` and no foreign-key errors.

Access to the user's existing Chrome tab was refused by the browser permission policy.
The navigation reproduction and verification therefore used only the disposable loopback
browser-test fixture, with no real guild configuration mutations.

## Selectable organization search — 2026-10-01

The guild organization form now searches by organization name or SID (up to 100 typed
characters) and displays a list containing both names and SIDs. Unmapped servers seed
the search with their Discord name after removing surrounding decorations such as stars.
Users must select a result with the mouse or arrow keys and Enter, then click Relier/Changer;
only the selected SID is submitted. Editing the search clears the selection and disables
saving. Earlier search results are discarded after a newer query or selection. Existing
mapping permissions and server-side validation remain in force.

This picker appears on both the Discord overview and the Config tab. Results stay inside
the panel, with readable names and SIDs on narrow screens. Browser tests verify suggested
matches without automatic mutations, long names, SID lookup, keyboard selection, empty
results, correct submitted identifiers, unchanged permissions and mobile width. All eight
Discord browser tests, all 423 web unit tests and the production build pass. The website
was restarted, preserving the running API/proxy and the real 168 members, 30 roles and two
syncs (SQLite integrity `ok`, no foreign-key violations).

The local test tracker still contains zero RSI organizations: its actual search will remain
empty until organizations are added or imported. Tests use a disposable fixture containing
sample organizations; no sample or invented RSI organization was inserted in the real database.

## Plugin storage review — 2026-10-01

The user clarified that Vencord only supplies the Discord management interface, collects the
requested guild on demand and sends it to the tracker. They explicitly selected persistence
of connection settings and checked guilds only. The SQLite database in the local test belongs
to the tracker API running on this PC, not to the plugin.

The plugin already had no saved roster or durable upload queue. Its remaining persisted
send summaries were removed from `definePluginSettings`: `lastResults` now lives only in
renderer session memory, clears on stop/start, and notifies the panel through the controller
subscription. Startup deletes summaries saved by previous versions. URL, certificate pin,
locally bound API key and selected guild IDs remain saved; collection bodies are temporary
and the tracker alone creates history.

Validation: **265 plugin tests passed**, local typecheck passed, and the pinned upstream
Vencord typecheck/build passed. An additional isolated runtime check executes the actual
controller with simulated Discord/native responses: two manual runs collect fresh data,
success/failure write no roster, result or retry queue, legacy summaries disappear,
stop/new renderer clears feedback, and connection/selection remain saved. The rebuilt
bundles are installed with matching hashes. Discord needs a renderer reload (Ctrl+R) to
activate this change; native transport is unchanged.

## Optional automatic-send timer — 2026-10-01

The user explicitly requested automatic sending of the checked guilds at a configured
interval, superseding the original manual-only scope. The plugin now exposes an optional
timer, disabled by default, with a 60-minute default interval (10 minutes to 7 days) and
the next-send date. Only these preferences are saved; deadlines, collected bodies and
send results remain session-only.

The first send waits a full interval, and the next interval starts after the batch ends.
Every occurrence reads the latest guild selection and uses the existing runner/transport.
Busy manual jobs, selection-empty states and credential writes skip the occurrence without
queueing catch-up work. Timer generations prevent reconfiguration and late completion from
creating overlaps or reviving a stopped timer. The visible stop button disables automatic
mode and stops the current batch; plugin stop removes the timer and settings listener while
preserving the selected timer preferences for the next startup.

Validation: **284 plugin tests / 18 files passed**, including fake-clock tests for first
delay, disabled/invalid imported settings, busy skips, long-running batches, latest selection,
reconfiguration, cancellation, errors and suspended clocks. Local and pinned Vencord
typecheck/build passed. The actual controller also passed an isolated simulated Discord
check of automatic ticks, latest selection, stop/listener cleanup and absence of roster,
result or queue writes. Updated bundles are installed and hash-checked; a Discord renderer
reload activates the new configuration panel. No real automatic send was initiated during
implementation.

## One-off context-menu send — 2026-10-01

At the user's request, every guild context menu now exposes “SC Tracker : envoyer ce
serveur”, including unchecked guilds. It collects and sends that guild once without
changing the selection or timer. Per-guild panel buttons use the same one-off behavior.
Batch/automatic sends retain checked-guild filtering; both modes share all configuration,
cancellation, validation and single-job protections. Automatic selection is rechecked after
the final asynchronous configuration read and immediately before IPC.

Validation: **290 tests / 18 files passed**, with regressions for unchecked one-off sends,
unchanged selection, cancellation, invalid IDs, configuration validation and the final
automatic-selection check. Local and pinned Vencord typecheck/build passed. An isolated
check of the actual menu/controller confirms the unchecked target is sent, the menu is
disabled while busy, selection/deadline remain unchanged, and no roster/results are saved.
Rebuilt bundles are installed and hash-verified; a renderer reload activates the menu.

## Local toolchain

**.NET SDK.**
- The earlier local SDK had disappeared from the machine.
- `dotnet-sdk-10.0.300-win-x64.zip` (runtime 10.0.8) was downloaded again from
  `builds.dotnet.microsoft.com`, with the user's approval.
- Its SHA-512 was checked against Microsoft's `release-metadata/10.0/releases.json`.
- It was extracted to `%LOCALAPPDATA%\sc-tracker-tools\dotnet-10.0.300`. No PATH or system
  installation was changed.
- Use it per command:
  `$env:DOTNET_ROOT = "$env:LOCALAPPDATA\sc-tracker-tools\dotnet-10.0.300"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"`.
- The EF tool (dotnet-ef 8) additionally needs `DOTNET_ROLL_FORWARD=Major`.

**Web.**
- The bundled pnpm does not match the pinned `pnpm@10.27.0`, so tools run through `node`:
  - `node node_modules/typescript/bin/tsc --noEmit`
  - `node node_modules/vitest/vitest.mjs run`
  - `node node_modules/next/dist/bin/next build`
- The installed Playwright Chromium revision does not match the package, so browser tests use
  the system Chrome (`E2E_BROWSER_CHANNEL=chrome`).

**Plugin.**
- The ignored `vencord/.tools/` holds the pinned Vencord checkout with its dependencies and the
  pnpm packages.
- To reinstall the plugin: `node vencord/install.mjs vencord/.tools/Vencord`.
- Then, in the checkout: `node node_modules/typescript/bin/tsc --noEmit` and
  `node --require=./scripts/suppressExperimentalWarnings.js scripts/build/build.mjs`.

## Targeted review (2026-10-01) — confirmed findings resolved

The changed working tree and the review notes were reread before resuming implementation.
The repository snapshot expectation fix was correct and retained. History integrity, resumable
writes, erasure, retention and additive migrations were also reread against the existing
regression tests; no further blocking defect was identified locally. The following confirmed
findings were fixed and covered by regression tests.

| Finding | Resolution and validation |
|---|---|
| Cloud Sync or settings import could redirect the locally saved API key | Save `{url, fingerprint, apiKey}` together in local DataStore `ScTracker_connection`; compare the canonical origin and pin with current settings before collection and again before IPC. Changed settings and legacy unbound keys refuse sending until explicitly saved. |
| An early HTTP reply could leave the upload request running | Destroy the request when settling, including when `writableFinished` is already true. A raw TLS early-reply test and deterministic request mocks cover both states. |
| Installer staging could be compiled as an extra plugin or remain after an error | Stage outside `src/`, throw through `finally`, remove only strictly named stale staging directories and use best-effort cleanup. Failure-injection and stale-directory tests pass. |
| The ingest key shared the owner's global JWT rate-limit budget | Partition scoped principals as `discord:ingest:user:{id}`. With the ingest budget exhausted, the owner's JWT still reads their profile and revokes the key. |
| Client correlation IDs were unrestricted | Accept one ASCII `X-Correlation-Id` of 1–64 characters matching `[A-Za-z0-9._:-]`; otherwise use the server trace identifier. Nine regression cases cover invalid input. |
| Discord's stock client drops the member-request nonce | Add the three nonce patches used by pinned Vencord's ImplicitRelationships. Patch matching and repeat application are unit-tested; absence of a correlated first reply still prevents upload. Real-client patch application remains an acceptance check. |
| Valid astral Unicode names could abort collection | Count Unicode code points, matching API validation. A 32-emoji name is accepted and 33 are rejected. |
| A later gateway or REST failure discarded earlier valid observations | Preserve members from correlated batches and return a partial result. Unknown protocol shapes and absence of any correlated reply still fail closed. |
| Role discovery could exhaust the call budget before freshness checks | Reserve gateway refresh calls, retain discovered candidates on budget/error stops and refuse zero-member uploads. |
| Receipt time was captured after waiting for the write gate | Capture arrival time in the filter before the gate wait and use it in ingestion. A fake-clock regression advances time while the gate is held. |
| Search indexing retries were too fast and never reset between pages | Wait at least five seconds for HTTP 202, respecting a longer `retry_after`; reset the counter after each successful page. |
| Duplicate search results could retain an older membership | Keep the observation with the newest `joined_at` without counting it as an additional unique member. |
| Coverage defaulted to normal exhaustion | Require an explicit stop reason; only normally exhausted member-search with matching positive counts can be complete. |

The native bridge validates all arguments and pins HTTPS before sending the key or body.
The renderer remains inside the trusted Vencord client boundary: other installed renderer
code can already access local DataStore. This review does not claim isolation from a
compromised client.

## Remaining

### Real-client test preparation (2026-10-01)

At the user's request, the existing desktop Discord/Vencord installation was identified.
The built plugin was copied into the already injected Vencord distribution after backing
up the prior distribution under `%LOCALAPPDATA%/sc-tracker-tools/vencord-backups/20261001-112958`.
It requires a full Discord restart. At that stage guild collection was manual; the client was
subsequently restarted as described below.

No SQLite database was found in this checkout. A dedicated local test database was created
at `data/discord-local-test/tracker.db` and migrated, including all nine Discord tables.
The API listens on `127.0.0.1:5000`, the website on `127.0.0.1:3000`, and a test-only HTTPS
proxy on `127.0.0.1:5443` forwards the fixed ingest path to the API. It uses a fresh local
certificate, never added to system trust. A local `discord:ingest` key expires after 24 hours.
Configuration, credentials, logs and process records stay in the ignored test directory.

The actual plugin transport was exercised against these services: a wrong pin sends
nothing; the valid pin and scoped key reach ingest validation (400 for an empty invalid
payload); an invalid key is refused (401); scoped keys cannot read roster routes (401);
the local administrator can read them (200), and the website login page returns 200.
No synthetic sample roster was inserted. The user selected their Liberastra server for the real test.

Windows Computer Use refused access to the Discord window with
`Computer Use was not approved to use Discord`. Client activation and sending therefore
remain manual until that access is available. The preparation alone did not establish
real-Discord acceptance; the subsequent manual send is recorded below.

The first attempt on Liberastra displayed `Le pont desktop SC Tracker est absent`.
Inspection confirmed `ScTracker.postSync` in the installed main-process bundle, the correct
plugin-name mapping in upstream IPC, and identical hashes for the installed and tested
native bundles. All Discord processes still dated from 10:00, before the distribution was
copied at 11:29: reloading the renderer had not restarted the main process. At 11:47 Paris
time on 2026-10-01, those exact Discord processes were stopped and the existing installation
relaunched through its updater launcher. New process start times were verified. The local
transport checks still pass.

### First real Discord sync accepted (2026-10-01, 11:49 Paris time)

The user's subsequent manual Liberastra send returned HTTP 200. The stored sync uses
`member-search`, with 168 expected and 168 collected members. The API independently
recognised it as complete and as the first baseline. The database contains 168 accounts
and memberships (157 humans, 11 bots) and 30 roles, with all join dates present, zero unknown
role references, zero opted-out observations and no departure guard trigger. The quiet
baseline intentionally creates no member events. No receipt remains pending.

Read-only SQLite `integrity_check` returns `ok`, with no foreign-key violations. Guild
detail, member, sync and event API reads return 200; the member count is 168 and the human
headcount is 157. The actual local guild web page also returns 200 and renders Liberastra
using a signed local-user session. This verifies the real small-guild member-search path
from Discord through native pinned HTTPS and API storage to the website. It does not verify
gateway role/cache fallback correlation or pagination above 1,000 members.

1. **Real Discord acceptance** (spec § 15, `vencord/README.md`), in the actual Discord client:
   - install the plugin, then do a full restart ;
   - create a key from the site ;
   - small-server complete mode: **passed on Liberastra**; partial mode remains ;
   - on a server of more than 1,000 members with member-search access, check that pages do not
     overlap and that their union reaches `total_result_count` ;
   - check the chunk nonce on the real client ;
   - test a wrong fingerprint.
2. **Production deployment**, not performed during the local implementation and review:
   - `deploy.sh <sha> --collector` for the tracker.db migration ;
   - the `COLLECTOR_API_Discord__Ingest__*` variables ;
   - the nginx zones and location, added only once the lot is deployed.

All A/B/C implementation lots and the confirmed review fixes are present locally. Nothing
was committed or pushed by this implementation session. Hosted CI has therefore not run on
these changes; its checks were exercised locally as listed above.
