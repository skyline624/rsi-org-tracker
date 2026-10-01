### Task C8: Web data layer, navigation and the /discord list

**Files:**
- Modify: `src/Collector.Web/src/lib/validation.ts` (after `ingestKeyExpiryDaysSchema`, added by A13)
- Modify: `src/Collector.Web/src/lib/api/types.ts` (after `DiscordIngestConfigDto`, in the "Discord" section added by A13)
- Modify: `src/Collector.Web/src/lib/api/endpoints.ts` (type imports; new section after `getDiscordIngestConfig`, added by A13)
- Create: `src/Collector.Web/src/app/(public)/discord/actions.ts`
- Create: `src/Collector.Web/src/lib/discord/format.ts`
- Create: `src/Collector.Web/src/components/discord/DiscordText.tsx`
- Create: `src/Collector.Web/src/components/discord/GuildIcon.tsx`
- Create: `src/Collector.Web/src/components/discord/RoleDot.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/GuildOrgForm.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/DiscordGuildsTable.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/page.tsx`
- Modify: `src/Collector.Web/src/components/layout/TopNav.tsx` (`NAV_ITEMS`)
- Test: `src/Collector.Web/src/lib/validation.test.ts` (import list; new cases at the end of the `describe`)
- Test: `src/Collector.Web/src/lib/api/endpoints.test.ts` (import line; new `describe` at the end)
- Test: `src/Collector.Web/src/app/action-arguments.test.ts` (imports; new cases after the `revokeApiKeyAction` block added by A13)
- Test: `src/Collector.Web/src/app/(public)/discord/actions.test.ts`
- Test: `src/Collector.Web/src/lib/discord/format.test.ts`
- Test: `src/Collector.Web/src/lib/discord/rendering-guard.test.ts`

**Interfaces:**
- Consumes:
  - CONTRACTS § 7 (routes of `DiscordRostersController`, JSON types): `GET api/discord/guilds`, `GET api/discord/guilds/{guildId}`, `…/members?status&search&rankRoleId&reconciliation&page&pageSize`, `…/events?type&userId&limit`, `…/syncs?limit`, `…/discrepancies`, `…/suggestions`, `PUT …/org { orgSid }` → `DiscordGuildSummaryDto`, `PUT …/roles/{roleId} { isRank, rankOrder, rsiRankLabel }` → `DiscordRoleDto`, `POST api/discord/links` → 201 `{ entityId, handle }`, `POST api/discord/link-rejections` → 201 `{ id }`, `DELETE api/discord/link-rejections/{id}`, `GET api/discord/multi?page&pageSize`, `GET api/users/{handle}/discord`, `GET api/organizations/{sid}/discord`.
  - CONTRACTS § 5 (lot A, admin routes): `DELETE api/discord/accounts/{discordUserId}` (204), `DELETE api/discord/guilds/{guildId}?exclude=true|false` (204/404), `POST api/discord/guilds/{guildId}/allow-mass-departure` (204/404), all AdminOnly.
  - C6 bounds (lot C, API): `rankOrder` 0–1000 or null, `rsiRankLabel` ≤ 100 characters or null (blank = null), `orgSid` null unmaps; refusals are `ValidationException` / `ForbiddenException` whose French message is the ProblemDetails `detail`.
  - Task A13 (already applied): `ApiKeyDto`, `CreatedApiKeyDto`, `DiscordIngestConfigDto` in `types.ts`; `listApiKeys`, `getDiscordIngestConfig` in `endpoints.ts`; `apiKeyNameSchema`, `ingestKeyExpiryDaysSchema` in `validation.ts`; the matching test edits in `validation.test.ts`, `endpoints.test.ts` and `action-arguments.test.ts`.
  - Existing: `apiGet` / `apiPost` / `apiPut` / `apiDelete` (`@/lib/api/client`, `query` option accepted by `apiDelete`), `ApiError` (`problem.detail`), `getSession`, `sessionCtx`, `Session` (`@/lib/auth/session`), `requireAuthCtx`, `withAuthRedirect` (`@/lib/auth/server-api`), `INVALID_ARGUMENTS`, `sidSchema`, `handleSchema`, `idSchema`, `citizenIdSchema`, `valid` (`@/lib/validation`), `searchOrgsAction(query: string): Promise<OrgOption[]>` and `OrgOption { sid; name }` (`@/app/(public)/users/[handle]/membership-actions`, the org search of the membership form), `HudPanel`, `HudBadge`, `HudButton`, `HudInput` (passes `list` through to its `<input>`), `HudDataGrid`/`HudColumn`, `formatNumber`, `cn`.
- Produces:
  - `types.ts`: `DiscordRankDto`, `DiscordLastSyncDto`, `DiscordRankCountDto`, `DiscordGuildSummaryDto`, `DiscordRoleDto`, `DiscordGuildDetailDto`, `DiscordLinkedPersonDto`, `DiscordReconciliation`, `DiscordMemberDto`, `DiscordEventDto`, `DiscordSyncDto`, `DiscordDiscrepancyKind`, `DiscordDiscrepancyDto`, `DiscordTotalsDto`, `DiscordDiscrepanciesDto`, `DiscordSuggestionConfidence`, `DiscordSuggestionDto`, `DiscordMultiGuildDto`, `DiscordRsiOrgDto`, `DiscordMultiMemberDto`, `DiscordProfileGuildDto`, `DiscordProfileAccountDto`, `DiscordTimelineEntryDto`, `DiscordUserProfileDto`, `DiscordOrgGuildDto`, `DiscordLinkCreatedDto`, `DiscordLinkRejectionCreatedDto`.
  - `endpoints.ts` (context first, as CONTRACTS § 7 writes them): `listDiscordGuilds(ctx)`, `getDiscordGuild(ctx, guildId)`, `getDiscordMembers(ctx, guildId, q: DiscordMembersQuery = {})`, `getDiscordEvents(ctx, guildId, q: DiscordEventsQuery = {})`, `getDiscordSyncs(ctx, guildId, limit = 50)`, `getDiscordDiscrepancies(ctx, guildId)`, `getDiscordSuggestions(ctx, guildId)`, `getDiscordMulti(ctx, q: { page?: number; pageSize?: number } = {})`, `getUserDiscord(ctx, handle)`, `getOrgDiscordGuilds(ctx, sid)`; interfaces `DiscordMembersQuery`, `DiscordEventsQuery`.
  - `validation.ts`: `snowflakeSchema` (`^[0-9]{17,20}$`), `rankOrderSchema` (integer 0–1000 or null), `rsiRankLabelSchema` (trimmed, ≤ 100, or null).
  - `src/app/(public)/discord/actions.ts` (`"use server"`), each returning `Promise<DiscordActionResult<T>>` = `{ ok: boolean; data?: T; error?: string }`, never throwing, `INVALID_ARGUMENTS` before any session or API call: `mapGuildOrgAction(guildId, orgSid)` → `DiscordGuildSummaryDto`, `updateGuildRoleAction(guildId, roleId, isRank, rankOrder, rsiRankLabel)` → `DiscordRoleDto`, `acceptSuggestionAction(discordUserId, citizenId, handle)` → `DiscordLinkCreatedDto`, `rejectSuggestionAction(discordUserId, citizenId, handle)` → `DiscordLinkRejectionCreatedDto`, `undoRejectionAction(id)`, and the admin-only `eraseAccountAction(discordUserId)`, `eraseGuildAction(guildId, exclude)`, `allowMassDepartureAction(guildId)` ("Réservé aux administrateurs." for a non-admin). Every parameter is `unknown`.
  - `src/lib/discord/format.ts`: `BadgeTone`, `BadgeSpec`, `cleanDiscordText`, `discordDisplayName`, `guildIconUrl`, `safeRoleColor`, `syncBadges`, `formatUtc`.
  - Components: `DiscordText({ value, fallback?, className? })`, `GuildIcon({ guildId, iconHash, size?, className? })`, `RoleDot({ color })` in `src/components/discord/`; client `GuildOrgForm({ guildId, currentSid, canEdit })`; client `DiscordGuildsTable({ rows })`; page `/discord`; nav item `DISCORD`.
  - Guard `src/lib/discord/rendering-guard.test.ts` with the list `DISCORD_UI` of scanned paths (C10 extends it).

- [ ] **Step 1: Write the failing schema tests**

In `src/Collector.Web/src/lib/validation.test.ts`, replace:

```ts
import {
  apiKeyNameSchema,
  handleSchema,
  idSchema,
  ingestKeyExpiryDaysSchema,
  linkProviderSchema,
  noteBodySchema,
  sidSchema,
  valid,
} from "./validation";
```

with:

```ts
import {
  apiKeyNameSchema,
  handleSchema,
  idSchema,
  ingestKeyExpiryDaysSchema,
  linkProviderSchema,
  noteBodySchema,
  rankOrderSchema,
  rsiRankLabelSchema,
  sidSchema,
  snowflakeSchema,
  valid,
} from "./validation";
```

Then replace:

```ts
  it.each([0, 366, 1.5, -1, "180", Number.NaN, null])("rejects an ingest key lifetime of %j", (days) => {
    expect(valid(ingestKeyExpiryDaysSchema, days)).toBe(false);
  });
});
```

with:

```ts
  it.each([0, 366, 1.5, -1, "180", Number.NaN, null])("rejects an ingest key lifetime of %j", (days) => {
    expect(valid(ingestKeyExpiryDaysSchema, days)).toBe(false);
  });

  it.each(["12345678901234567", "123456789012345678", "12345678901234567890"])("accepts snowflake %s", (id) => {
    expect(valid(snowflakeSchema, id)).toBe(true);
  });

  it.each([
    "1234567890123456",
    "123456789012345678901",
    "12345678901234567a",
    " 123456789012345678",
    "../123456789012345678",
    123456789012,
    null,
  ])("rejects snowflake %j", (id) => {
    expect(valid(snowflakeSchema, id)).toBe(false);
  });

  it.each([0, 12, 1000, null])("accepts rank order %j", (order) => {
    expect(valid(rankOrderSchema, order)).toBe(true);
  });

  it.each([-1, 1001, 1.5, "3", undefined, Number.NaN])("rejects rank order %j", (order) => {
    expect(valid(rankOrderSchema, order)).toBe(false);
  });

  it.each(["Officer", "  Director  ", "x".repeat(100), null])("accepts RSI rank label %j", (label) => {
    expect(valid(rsiRankLabelSchema, label)).toBe(true);
  });

  it.each(["x".repeat(101), 42, undefined])("rejects RSI rank label %j", (label) => {
    expect(valid(rsiRankLabelSchema, label)).toBe(false);
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/validation.test.ts`

Expected: `FAIL  src/lib/validation.test.ts`; the 27 new cases fail with `TypeError: Cannot read properties of undefined (reading 'safeParse')` (the three schemas do not exist yet); the existing cases pass.

- [ ] **Step 3: Write the minimal implementation (schemas)**

In `src/Collector.Web/src/lib/validation.ts`, replace:

```ts
/** Lifetime of a Discord ingest key, in whole days: the API refuses more than 365. */
export const ingestKeyExpiryDaysSchema = z.number().int().min(1).max(365);
```

with:

```ts
/** Lifetime of a Discord ingest key, in whole days: the API refuses more than 365. */
export const ingestKeyExpiryDaysSchema = z.number().int().min(1).max(365);

/** Discord id (account, server or role): a snowflake, carried as text from end to end. */
export const snowflakeSchema = z.string().regex(/^[0-9]{17,20}$/);

/** Order of a Discord rank role (the API accepts 0 to 1000); null keeps or clears it. */
export const rankOrderSchema = z.number().int().min(0).max(1000).nullable();

/** RSI rank a Discord rank stands for (the API stores up to 100 characters); null clears it. */
export const rsiRankLabelSchema = z.string().trim().max(100).nullable();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/validation.test.ts`

Expected: `Test Files  1 passed (1)`, no failed test.

- [ ] **Step 5: Write the failing endpoint tests**

In `src/Collector.Web/src/lib/api/endpoints.test.ts`, replace:

```ts
import { getDiscordIngestConfig, getOrgMembersPage, listApiKeys } from "./endpoints";
```

with:

```ts
import {
  getDiscordDiscrepancies,
  getDiscordEvents,
  getDiscordGuild,
  getDiscordIngestConfig,
  getDiscordMembers,
  getDiscordMulti,
  getDiscordSuggestions,
  getDiscordSyncs,
  getOrgDiscordGuilds,
  getOrgMembersPage,
  getUserDiscord,
  listApiKeys,
  listDiscordGuilds,
} from "./endpoints";
```

Then append at the end of the file:

```ts

describe("Discord roster endpoints", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const ctx = { bearerToken: "t" };
  const guildId = "123456789012345678";

  function stub(body: unknown) {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  function urls(fetchMock: ReturnType<typeof stub>) {
    return fetchMock.mock.calls.map((call) => new URL(String((call as unknown[])[0])));
  }

  it.each([
    ["the server list", () => listDiscordGuilds(ctx), "/api/discord/guilds"],
    ["a server", () => getDiscordGuild(ctx, guildId), `/api/discord/guilds/${guildId}`],
    ["its RSI discrepancies", () => getDiscordDiscrepancies(ctx, guildId), `/api/discord/guilds/${guildId}/discrepancies`],
    ["its link suggestions", () => getDiscordSuggestions(ctx, guildId), `/api/discord/guilds/${guildId}/suggestions`],
    ["a citizen's cross profile", () => getUserDiscord(ctx, "Pilote42"), "/api/users/Pilote42/discord"],
    ["an org's servers", () => getOrgDiscordGuilds(ctx, "CORP"), "/api/organizations/CORP/discord"],
  ])("reads %s with the user's token", async (_, call, path) => {
    const fetchMock = stub([]);

    await call();

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(new URL(url).pathname).toBe(path);
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer t");
  });

  it("encodes every path segment it is given", async () => {
    const fetchMock = stub({});

    await getDiscordGuild(ctx, "1/2?x");
    await getUserDiscord(ctx, "a b#c");
    await getOrgDiscordGuilds(ctx, "A/B");

    const called = urls(fetchMock);
    expect(called.map((u) => u.pathname)).toEqual([
      "/api/discord/guilds/1%2F2%3Fx",
      "/api/users/a%20b%23c/discord",
      "/api/organizations/A%2FB/discord",
    ]);
    expect(called.every((u) => u.search === "")).toBe(true);
  });

  it("asks the API for one page of members, with the page's filters", async () => {
    const fetchMock = stub({ items: [], total: 0, page: 2, pageSize: 50, totalPages: 0 });

    await getDiscordMembers(ctx, guildId, {
      status: "former",
      search: "pilote",
      rankRoleId: "223456789012345678",
      reconciliation: "rank_mismatch",
      page: 2,
      pageSize: 50,
    });

    const [url] = urls(fetchMock);
    expect(url?.pathname).toBe(`/api/discord/guilds/${guildId}/members`);
    expect(Object.fromEntries(url?.searchParams ?? [])).toEqual({
      status: "former",
      search: "pilote",
      rankRoleId: "223456789012345678",
      reconciliation: "rank_mismatch",
      page: "2",
      pageSize: "50",
    });
  });

  it("leaves unset member filters out of the query", async () => {
    const fetchMock = stub({ items: [], total: 0, page: 1, pageSize: 50, totalPages: 0 });

    await getDiscordMembers(ctx, guildId, { status: "active", search: undefined, page: 1, pageSize: 50 });

    expect(Object.fromEntries(urls(fetchMock)[0]?.searchParams ?? [])).toEqual({
      status: "active",
      page: "1",
      pageSize: "50",
    });
  });

  it("bounds the history, the upload journal and the multi-membership page", async () => {
    const fetchMock = stub([]);

    await getDiscordEvents(ctx, guildId, { type: "left", userId: "323456789012345678", limit: 100 });
    await getDiscordSyncs(ctx, guildId, 20);
    await getDiscordMulti(ctx, { page: 3, pageSize: 50 });

    expect(urls(fetchMock).map((u) => [u.pathname, Object.fromEntries(u.searchParams)])).toEqual([
      [`/api/discord/guilds/${guildId}/events`, { type: "left", userId: "323456789012345678", limit: "100" }],
      [`/api/discord/guilds/${guildId}/syncs`, { limit: "20" }],
      ["/api/discord/multi", { page: "3", pageSize: "50" }],
    ]);
  });
});
```

- [ ] **Step 6: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/api/endpoints.test.ts`

Expected: `FAIL  src/lib/api/endpoints.test.ts > Discord roster endpoints`, every new test with `TypeError: listDiscordGuilds is not a function` (or the name of the first missing function it calls); `getOrgMembersPage` and `settings endpoints` still pass.

- [ ] **Step 7: Write the minimal implementation (types and endpoints)**

In `src/Collector.Web/src/lib/api/types.ts`, replace:

```ts
// ── Discord ─────────────────────────────────────────────────
/** What to enter in the Vencord plugin; null while the administrator has not set it. */
export interface DiscordIngestConfigDto {
  publicUrl: string | null;
  certificateSha256: string | null;
}
```

with:

```ts
// ── Discord ─────────────────────────────────────────────────
/** What to enter in the Vencord plugin; null while the administrator has not set it. */
export interface DiscordIngestConfigDto {
  publicUrl: string | null;
  certificateSha256: string | null;
}

// ── Discord rosters (CONTRACTS § 7) ─────────────────────────
// Every Discord id is a string (snowflake). Names come from Discord users: render
// them through components/discord/DiscordText, never as HTML.

/** A Discord role shown for a member: their rank, or one of their roles. */
export interface DiscordRankDto {
  roleId: string;
  name: string;
  /** "#rrggbb", or null for Discord's default colour. */
  color: string | null;
}

/** The last upload accepted for a server. */
export interface DiscordLastSyncDto {
  receivedAt: string;
  isComplete: boolean;
  method: string;
  submittedBy: string;
  departureGuardTripped: boolean;
}

/** Active non-bot members holding one rank. */
export interface DiscordRankCountDto {
  roleId: string;
  name: string;
  color: string | null;
  count: number;
}

export interface DiscordGuildSummaryDto {
  guildId: string;
  name: string;
  iconHash: string | null;
  orgSid: string | null;
  orgName: string | null;
  /** Username of the responsible user (who mapped the server); null while unmapped. */
  orgMappedBy: string | null;
  /** Non-bot members present (LeftAt null). */
  activeMembers: number;
  rankDistribution: DiscordRankCountDto[];
  lastSync: DiscordLastSyncDto | null;
  lastCompleteSyncAt: string | null;
}

export interface DiscordRoleDto {
  roleId: string;
  name: string;
  position: number;
  color: string | null;
  hoist: boolean;
  managed: boolean;
  isRank: boolean;
  rankOrder: number | null;
  rsiRankLabel: string | null;
  deleted: boolean;
  memberCount: number;
}

export interface DiscordGuildDetailDto extends DiscordGuildSummaryDto {
  /** Every role of the server, deleted ones included. */
  roles: DiscordRoleDto[];
  /** RSI ranks known for the mapped org, offered as rank equivalents. */
  rsiRanks: string[];
  /** The caller may map the org and configure ranks (unmapped server, responsible user or admin). */
  canEdit: boolean;
}

/** A person an account is linked to by a validated link. */
export interface DiscordLinkedPersonDto {
  handle: string | null;
  citizenId: number | null;
  displayName: string | null;
}

export type DiscordReconciliation = "rsi_unknown" | "unlinked" | "ok" | "rank_mismatch" | "not_in_rsi_org";

export interface DiscordMemberDto {
  discordUserId: string;
  username: string;
  globalName: string | null;
  nick: string | null;
  isBot: boolean;
  joinedAt: string | null;
  firstSeenAt: string;
  lastSeenAt: string;
  leftAt: string | null;
  rank: DiscordRankDto | null;
  roles: DiscordRankDto[];
  links: DiscordLinkedPersonDto[];
  rsiRank: string | null;
  /** Null when the server is unmapped or the member is a bot. */
  reconciliation: DiscordReconciliation | null;
  multipleLinks: boolean;
}

export interface DiscordEventDto {
  id: number;
  /** Null for an account-level event (username or global name). */
  guildId: string | null;
  discordUserId: string;
  username: string | null;
  /** joined, left, rejoined, roles_changed, nick_changed, username_changed, global_name_changed. */
  type: string;
  oldValue: string | null;
  newValue: string | null;
  /** Exact date when known; otherwise the event lies between notBefore and observedAt. */
  occurredAt: string | null;
  notBefore: string | null;
  observedAt: string;
  /** Who sent the upload that recorded it. */
  submittedBy: string | null;
  /** Set when a roles change changes the member's rank. */
  rankChange: { from: string | null; to: string | null } | null;
}

export interface DiscordSyncDto {
  id: number;
  receivedAt: string;
  collectedAt: string;
  submittedBy: string;
  method: string;
  declaredComplete: boolean;
  isComplete: boolean;
  isBaseline: boolean;
  departureGuardTripped: boolean;
  expectedCount: number | null;
  collectedCount: number;
  optedOutCount: number;
  unknownRoleRefCount: number;
  eventCount: number;
  pluginVersion: string;
}

export type DiscordDiscrepancyKind = "rsi_only" | "not_in_rsi_org" | "rank_mismatch";

export interface DiscordDiscrepancyDto {
  kind: DiscordDiscrepancyKind;
  handle: string | null;
  citizenId: number | null;
  discordUserId: string | null;
  discordName: string | null;
  discordRank: string | null;
  rsiRank: string | null;
}

export interface DiscordTotalsDto {
  discordActive: number;
  discordLinked: number;
  rsiVisible: number | null;
  rsiRedacted: number | null;
  rsiHidden: number | null;
  rsiTotalRows: number | null;
  rsiCountsAt: string | null;
  /** False when the last RSI read was incomplete: only rsiTotalRows is known. */
  rsiBreakdownKnown: boolean;
}

export interface DiscordDiscrepanciesDto {
  /** Null for an unmapped server: items is then empty and totals null. */
  orgSid: string | null;
  /** False until a complete upload: who is missing from Discord is then unknown. */
  rsiOnlyAvailable: boolean;
  items: DiscordDiscrepancyDto[];
  totals: DiscordTotalsDto | null;
}

export type DiscordSuggestionConfidence = "strong" | "medium";

export interface DiscordSuggestionDto {
  discordUserId: string;
  discordName: string;
  matchedToken: string;
  /** Current canonical RSI handle, whichever handle the token matched. */
  handle: string;
  citizenId: number | null;
  displayName: string | null;
  confidence: DiscordSuggestionConfidence;
}

export interface DiscordMultiGuildDto {
  guildId: string;
  guildName: string;
  orgSid: string | null;
  rank: string | null;
}

export interface DiscordRsiOrgDto {
  sid: string;
  rank: string | null;
}

export interface DiscordMultiMemberDto {
  discordUserId: string;
  username: string;
  globalName: string | null;
  guilds: DiscordMultiGuildDto[];
  links: DiscordLinkedPersonDto[];
  /** Active RSI orgs of every linked person. */
  rsiOrgs: DiscordRsiOrgDto[];
}

export interface DiscordProfileGuildDto {
  guildId: string;
  guildName: string;
  orgSid: string | null;
  rank: string | null;
  joinedAt: string | null;
  leftAt: string | null;
  lastSeenAt: string;
}

export interface DiscordProfileAccountDto {
  discordUserId: string;
  username: string;
  globalName: string | null;
  guilds: DiscordProfileGuildDto[];
}

export interface DiscordTimelineEntryDto {
  source: "rsi" | "discord";
  type: string;
  at: string;
  notBefore: string | null;
  orgSid: string | null;
  guildId: string | null;
  guildName: string | null;
  oldValue: string | null;
  newValue: string | null;
}

export interface DiscordUserProfileDto {
  accounts: DiscordProfileAccountDto[];
  /** At most 100 entries, newest first. */
  timeline: DiscordTimelineEntryDto[];
}

export interface DiscordOrgGuildDto {
  guildId: string;
  name: string;
  iconHash: string | null;
  activeMembers: number;
  linkedMembers: number;
  lastSyncAt: string;
  lastSyncComplete: boolean;
}

/** 201 of POST api/discord/links. */
export interface DiscordLinkCreatedDto {
  entityId: number;
  handle: string;
}

/** 201 of POST api/discord/link-rejections: the id undoes the rejection. */
export interface DiscordLinkRejectionCreatedDto {
  id: number;
}
```

In `src/Collector.Web/src/lib/api/endpoints.ts`, replace:

```ts
  CycleStatusDto,
  DiscordIngestConfigDto,
  GrowthDataPoint,
```

with:

```ts
  CycleStatusDto,
  DiscordDiscrepanciesDto,
  DiscordEventDto,
  DiscordGuildDetailDto,
  DiscordGuildSummaryDto,
  DiscordIngestConfigDto,
  DiscordMemberDto,
  DiscordMultiMemberDto,
  DiscordOrgGuildDto,
  DiscordReconciliation,
  DiscordSuggestionDto,
  DiscordSyncDto,
  DiscordUserProfileDto,
  GrowthDataPoint,
```

Then replace:

```ts
// ── Discord ingest ──────────────────────────────────────────
/** Public URL and certificate fingerprint to enter in the Vencord plugin. */
export const getDiscordIngestConfig = (ctx: Ctx = {}) =>
  apiGet<DiscordIngestConfigDto>("/api/discord/ingest-config", undefined, ctx);
```

with:

```ts
// ── Discord ingest ──────────────────────────────────────────
/** Public URL and certificate fingerprint to enter in the Vencord plugin. */
export const getDiscordIngestConfig = (ctx: Ctx = {}) =>
  apiGet<DiscordIngestConfigDto>("/api/discord/ingest-config", undefined, ctx);

// ── Discord rosters ─────────────────────────────────────────
// CONTRACTS § 7: the context comes first, and every id put in a path is encoded.
const discordGuildPath = (guildId: string) =>
  `/api/discord/guilds/${encodeURIComponent(guildId)}`;

/** Tracked servers: unmapped first, then by name. */
export const listDiscordGuilds = (ctx: Ctx) =>
  apiGet<DiscordGuildSummaryDto[]>("/api/discord/guilds", undefined, ctx);

/** A server with its roles and whether the caller may configure it (404 if unknown). */
export const getDiscordGuild = (ctx: Ctx, guildId: string) =>
  apiGet<DiscordGuildDetailDto>(discordGuildPath(guildId), undefined, ctx);

export interface DiscordMembersQuery {
  status?: "active" | "former" | "all";
  search?: string;
  rankRoleId?: string;
  reconciliation?: DiscordReconciliation;
  page?: number;
  pageSize?: number;
  [k: string]: string | number | boolean | undefined | null;
}

/** One page of a server's members, paged by the API. */
export const getDiscordMembers = (ctx: Ctx, guildId: string, q: DiscordMembersQuery = {}) =>
  apiGet<PaginatedResponse<DiscordMemberDto>>(`${discordGuildPath(guildId)}/members`, q, ctx);

export interface DiscordEventsQuery {
  type?: string;
  userId?: string;
  limit?: number;
  [k: string]: string | number | boolean | undefined | null;
}

/** A server's history, newest first. */
export const getDiscordEvents = (ctx: Ctx, guildId: string, q: DiscordEventsQuery = {}) =>
  apiGet<DiscordEventDto[]>(`${discordGuildPath(guildId)}/events`, q, ctx);

/** A server's upload journal, newest first. */
export const getDiscordSyncs = (ctx: Ctx, guildId: string, limit = 50) =>
  apiGet<DiscordSyncDto[]>(`${discordGuildPath(guildId)}/syncs`, { limit }, ctx);

/** Gaps between a server and the RSI roster of its org, with totals. */
export const getDiscordDiscrepancies = (ctx: Ctx, guildId: string) =>
  apiGet<DiscordDiscrepanciesDto>(`${discordGuildPath(guildId)}/discrepancies`, undefined, ctx);

/** Proposed Discord ↔ RSI links for the server's unlinked members. */
export const getDiscordSuggestions = (ctx: Ctx, guildId: string) =>
  apiGet<DiscordSuggestionDto[]>(`${discordGuildPath(guildId)}/suggestions`, undefined, ctx);

/** Accounts present on at least two tracked servers, one page. */
export const getDiscordMulti = (ctx: Ctx, q: { page?: number; pageSize?: number } = {}) =>
  apiGet<PaginatedResponse<DiscordMultiMemberDto>>(
    "/api/discord/multi",
    { page: q.page, pageSize: q.pageSize },
    ctx,
  );

/** A citizen's linked Discord accounts, their servers and the combined timeline. */
export const getUserDiscord = (ctx: Ctx, handle: string) =>
  apiGet<DiscordUserProfileDto>(`/api/users/${encodeURIComponent(handle)}/discord`, undefined, ctx);

/** Discord servers mapped to an org (empty list when none). */
export const getOrgDiscordGuilds = (ctx: Ctx, sid: string) =>
  apiGet<DiscordOrgGuildDto[]>(`/api/organizations/${encodeURIComponent(sid)}/discord`, undefined, ctx);
```

- [ ] **Step 8: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/api/endpoints.test.ts`

Expected: `Test Files  1 passed (1)`; the 10 new tests of `Discord roster endpoints` pass with the earlier ones.

- [ ] **Step 9: Write the failing server action tests**

In `src/Collector.Web/src/app/action-arguments.test.ts`, replace:

```ts
const { createDiscordIngestKeyAction, revokeApiKeyAction } = await import("./(user)/settings/discord-key-actions");
```

with:

```ts
const { createDiscordIngestKeyAction, revokeApiKeyAction } = await import("./(user)/settings/discord-key-actions");
const discord = await import("./(public)/discord/actions");
```

Then replace:

```ts
  it.each([
    ["a path as id", () => revokeApiKeyAction("../admin/users/3")],
    ["an id as text", () => revokeApiKeyAction("12")],
    ["a negative id", () => revokeApiKeyAction(-1)],
  ])("revokeApiKeyAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });
```

with:

```ts
  it.each([
    ["a path as id", () => revokeApiKeyAction("../admin/users/3")],
    ["an id as text", () => revokeApiKeyAction("12")],
    ["a negative id", () => revokeApiKeyAction(-1)],
  ])("revokeApiKeyAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  // Discord ids end up in API paths: only snowflakes get through.
  const GUILD = "123456789012345678";
  const ROLE = "223456789012345678";
  const USER = "323456789012345678";

  it.each([
    ["a path as server id", () => discord.mapGuildOrgAction("../admin", "CORP")],
    ["a server id as a number", () => discord.mapGuildOrgAction(123456789012, "CORP")],
    ["a SID with a path", () => discord.mapGuildOrgAction(GUILD, "../x")],
    ["a SID too long", () => discord.mapGuildOrgAction(GUILD, "WAYTOOLONGSID")],
    ["no SID (null unmaps)", () => discord.mapGuildOrgAction(GUILD, undefined)],
  ])("mapGuildOrgAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a path as role id", () => discord.updateGuildRoleAction(GUILD, "../1", true, 1, null)],
    ["a text rank flag", () => discord.updateGuildRoleAction(GUILD, ROLE, "true", 1, null)],
    ["a negative order", () => discord.updateGuildRoleAction(GUILD, ROLE, true, -1, null)],
    ["an order beyond 1000", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1001, null)],
    ["a fractional order", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1.5, null)],
    ["an order as text", () => discord.updateGuildRoleAction(GUILD, ROLE, true, "3", null)],
    ["no order (null clears it)", () => discord.updateGuildRoleAction(GUILD, ROLE, true, undefined, null)],
    ["an RSI rank too long", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1, "x".repeat(101))],
    ["an RSI rank as a number", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1, 42)],
  ])("updateGuildRoleAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  const linkCases = [
    ["a path as account id", "../1", 42, "Pilote42"],
    ["an account id too short", "1234", 42, "Pilote42"],
    ["a citizen number as text", USER, "42", "Pilote42"],
    ["a negative citizen number", USER, -1, "Pilote42"],
    ["no citizen number (null expected)", USER, undefined, "Pilote42"],
    ["a handle with a path", USER, 42, "../admin"],
    ["no handle", USER, null, undefined],
  ] as const;

  it.each(linkCases)("acceptSuggestionAction: %s", async (_, user, citizen, handle) => {
    expect(await discord.acceptSuggestionAction(user, citizen, handle)).toEqual(invalid);
    noApiCall();
  });

  it.each(linkCases)("rejectSuggestionAction: %s", async (_, user, citizen, handle) => {
    expect(await discord.rejectSuggestionAction(user, citizen, handle)).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["an id as text", () => discord.undoRejectionAction("12")],
    ["a negative id", () => discord.undoRejectionAction(-1)],
    ["a path as id", () => discord.undoRejectionAction("../admin")],
  ])("undoRejectionAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a path as account id", () => discord.eraseAccountAction("../admin")],
    ["an account id too short", () => discord.eraseAccountAction("12")],
    ["an account id as a number", () => discord.eraseAccountAction(42)],
  ])("eraseAccountAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a path as server id", () => discord.eraseGuildAction("../x", true)],
    ["a text exclude flag", () => discord.eraseGuildAction(GUILD, "true")],
    ["no exclude flag", () => discord.eraseGuildAction(GUILD, undefined)],
  ])("eraseGuildAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a path as server id", () => discord.allowMassDepartureAction("../x")],
    ["no server id", () => discord.allowMassDepartureAction(undefined)],
  ])("allowMassDepartureAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });
```

Create `src/Collector.Web/src/app/(public)/discord/actions.test.ts`:

```ts
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("next/headers", () => ({ cookies: async () => ({ set: vi.fn() }), headers: async () => new Headers() }));
vi.mock("@/lib/auth/session", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/auth/session")>()),
  getSession: vi.fn(),
}));
vi.mock("@/lib/api/client", () => ({ apiGet: vi.fn(), apiPost: vi.fn(), apiPut: vi.fn(), apiDelete: vi.fn() }));

const { getSession } = await import("@/lib/auth/session");
const { apiDelete, apiGet, apiPost, apiPut } = await import("@/lib/api/client");
const { ApiError } = await import("@/lib/api/errors");
const { INVALID_ARGUMENTS } = await import("@/lib/validation");
const {
  acceptSuggestionAction,
  allowMassDepartureAction,
  eraseAccountAction,
  eraseGuildAction,
  mapGuildOrgAction,
  rejectSuggestionAction,
  undoRejectionAction,
  updateGuildRoleAction,
} = await import("./actions");

const GUILD = "123456789012345678";
const ROLE = "223456789012345678";
const USER = "323456789012345678";

const member = {
  userId: 2,
  username: "pilot",
  isAdmin: false,
  accessToken: "jwt",
  clientIp: "203.0.113.7",
  expiresAt: new Date(),
};
const admin = { ...member, userId: 1, username: "admin", isAdmin: true };

/** What sessionCtx() gives the API client for these sessions. */
const ctx = { bearerToken: "jwt", clientIp: "203.0.113.7" };

function expectNoApiCall() {
  for (const fn of [apiGet, apiPost, apiPut, apiDelete]) expect(fn).not.toHaveBeenCalled();
}

// Braces matter: a function returned by beforeEach runs as its teardown.
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(getSession).mockResolvedValue(member);
});

describe("mapGuildOrgAction", () => {
  it("maps the server to the SID typed, trimmed", async () => {
    const summary = { guildId: GUILD, orgSid: "CORP" };
    vi.mocked(apiPut).mockResolvedValue(summary);

    expect(await mapGuildOrgAction(GUILD, " CORP ")).toEqual({ ok: true, data: summary });
    expect(apiPut).toHaveBeenCalledWith(`/api/discord/guilds/${GUILD}/org`, { orgSid: "CORP" }, ctx);
  });

  it("unmaps the server with null", async () => {
    vi.mocked(apiPut).mockResolvedValue({ guildId: GUILD, orgSid: null });

    expect((await mapGuildOrgAction(GUILD, null)).ok).toBe(true);
    expect(apiPut).toHaveBeenCalledWith(`/api/discord/guilds/${GUILD}/org`, { orgSid: null }, ctx);
  });

  it("shows the API's own reason when another user is responsible for the server", async () => {
    const reason = "Ce serveur est relié par alice : seuls ce responsable et les administrateurs peuvent le modifier.";
    vi.mocked(apiPut).mockRejectedValue(new ApiError(403, { title: "Forbidden", status: 403, detail: reason }));

    expect(await mapGuildOrgAction(GUILD, "CORP")).toEqual({ ok: false, error: reason });
  });
});

describe("updateGuildRoleAction", () => {
  it("sends the rank settings, with the RSI rank trimmed", async () => {
    const role = { roleId: ROLE, isRank: true, rankOrder: 5, rsiRankLabel: "Officer" };
    vi.mocked(apiPut).mockResolvedValue(role);

    expect(await updateGuildRoleAction(GUILD, ROLE, true, 5, "  Officer ")).toEqual({ ok: true, data: role });
    expect(apiPut).toHaveBeenCalledWith(
      `/api/discord/guilds/${GUILD}/roles/${ROLE}`,
      { isRank: true, rankOrder: 5, rsiRankLabel: "Officer" },
      ctx,
    );
  });

  it("clears the order and a blank RSI rank with null", async () => {
    vi.mocked(apiPut).mockResolvedValue({});

    await updateGuildRoleAction(GUILD, ROLE, false, null, "   ");

    expect(apiPut).toHaveBeenCalledWith(
      `/api/discord/guilds/${GUILD}/roles/${ROLE}`,
      { isRank: false, rankOrder: null, rsiRankLabel: null },
      ctx,
    );
  });
});

describe("suggestions", () => {
  it("validates a suggestion as a link to the citizen", async () => {
    vi.mocked(apiPost).mockResolvedValue({ entityId: 7, handle: "Pilote42" });

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({
      ok: true,
      data: { entityId: 7, handle: "Pilote42" },
    });
    expect(apiPost).toHaveBeenCalledWith(
      "/api/discord/links",
      { discordUserId: USER, citizenId: 42, handle: "Pilote42" },
      ctx,
    );
  });

  it("validates a suggestion for a citizen without a known number", async () => {
    vi.mocked(apiPost).mockResolvedValue({ entityId: 8, handle: "pilote42" });

    await acceptSuggestionAction(USER, null, "pilote42");

    expect(apiPost).toHaveBeenCalledWith(
      "/api/discord/links",
      { discordUserId: USER, citizenId: null, handle: "pilote42" },
      ctx,
    );
  });

  it("ignores a suggestion and returns the rejection id", async () => {
    vi.mocked(apiPost).mockResolvedValue({ id: 9 });

    expect(await rejectSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: true, data: { id: 9 } });
    expect(apiPost).toHaveBeenCalledWith(
      "/api/discord/link-rejections",
      { discordUserId: USER, citizenId: 42, handle: "Pilote42" },
      ctx,
    );
  });

  it("undoes a rejection by its id", async () => {
    vi.mocked(apiDelete).mockResolvedValue(undefined);

    expect(await undoRejectionAction(9)).toEqual({ ok: true });
    expect(apiDelete).toHaveBeenCalledWith("/api/discord/link-rejections/9", ctx);
  });
});

describe("admin-only actions", () => {
  it.each([
    ["eraseAccountAction", () => eraseAccountAction(USER)],
    ["eraseGuildAction, excluding the server", () => eraseGuildAction(GUILD, true)],
    ["eraseGuildAction, back to a baseline", () => eraseGuildAction(GUILD, false)],
    ["allowMassDepartureAction", () => allowMassDepartureAction(GUILD)],
  ])("%s refuses a member who is not an admin", async (_, call) => {
    expect(await call()).toEqual({ ok: false, error: "Réservé aux administrateurs." });
    expectNoApiCall();
  });

  describe("as an admin", () => {
    beforeEach(() => {
      vi.mocked(getSession).mockResolvedValue(admin);
    });

    it("erases and excludes an account", async () => {
      vi.mocked(apiDelete).mockResolvedValue(undefined);

      expect(await eraseAccountAction(USER)).toEqual({ ok: true });
      expect(apiDelete).toHaveBeenCalledWith(`/api/discord/accounts/${USER}`, ctx);
    });

    it.each([true, false])("deletes a server with exclude=%s", async (exclude) => {
      vi.mocked(apiDelete).mockResolvedValue(undefined);

      expect(await eraseGuildAction(GUILD, exclude)).toEqual({ ok: true });
      expect(apiDelete).toHaveBeenCalledWith(`/api/discord/guilds/${GUILD}`, { ...ctx, query: { exclude } });
    });

    it("lets the next complete upload record a mass departure", async () => {
      vi.mocked(apiPost).mockResolvedValue(undefined);

      expect(await allowMassDepartureAction(GUILD)).toEqual({ ok: true });
      expect(apiPost).toHaveBeenCalledWith(`/api/discord/guilds/${GUILD}/allow-mass-departure`, undefined, ctx);
    });
  });
});

describe("failures are answers, never exceptions", () => {
  it("refuses an anonymous caller", async () => {
    vi.mocked(getSession).mockResolvedValue(null);

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: false, error: "Non authentifié." });
    expectNoApiCall();
  });

  it("checks the arguments before looking at the session", async () => {
    vi.mocked(getSession).mockResolvedValue(null);

    expect(await eraseGuildAction("../x", true)).toEqual({ ok: false, error: INVALID_ARGUMENTS });
    expect(getSession).not.toHaveBeenCalled();
  });

  it("falls back to the status title when the API gives no detail", async () => {
    vi.mocked(apiPost).mockRejectedValue(new ApiError(404, { title: "Not Found", status: 404 }));

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: false, error: "Not Found" });
  });

  it("gives a French message for anything that is not an Error", async () => {
    vi.mocked(apiPost).mockRejectedValue("boom");

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({
      ok: false,
      error: "Échec de la validation du lien.",
    });
  });
});
```

- [ ] **Step 10: Run them to verify they fail**

Run (from `src/Collector.Web`): `pnpm exec vitest run "src/app/(public)/discord/actions.test.ts" src/app/action-arguments.test.ts`

Expected: both files `FAIL` before any test runs, with `Error: Cannot find module '/src/app/(public)/discord/actions' imported from …` (Vite may word it `Failed to load url ./actions`): the module does not exist yet.

- [ ] **Step 11: Write the minimal implementation (server actions)**

Create `src/Collector.Web/src/app/(public)/discord/actions.ts`:

```ts
"use server";

import { z } from "zod";
import { apiDelete, apiPost, apiPut } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import type {
  DiscordGuildSummaryDto,
  DiscordLinkCreatedDto,
  DiscordLinkRejectionCreatedDto,
  DiscordRoleDto,
} from "@/lib/api/types";
import { getSession, sessionCtx } from "@/lib/auth/session";
import {
  INVALID_ARGUMENTS,
  citizenIdSchema,
  handleSchema,
  idSchema,
  rankOrderSchema,
  rsiRankLabelSchema,
  sidSchema,
  snowflakeSchema,
} from "@/lib/validation";

/**
 * Discord roster mutations (spec § 11 and § 13.2). Any client can call a server action
 * with any values: every argument is validated first, so a malformed call never reaches
 * the session or the API, and every outcome is returned, never thrown.
 */
export interface DiscordActionResult<T = undefined> {
  ok: boolean;
  data?: T;
  error?: string;
}

type ApiCtx = ReturnType<typeof sessionCtx>;

const NOT_SIGNED_IN = "Non authentifié.";
const ADMIN_ONLY = "Réservé aux administrateurs.";

const orgSidSchema = sidSchema.nullable();
const optionalCitizenIdSchema = citizenIdSchema.nullable();
const flagSchema = z.boolean();

const guildPath = (guildId: string) => `/api/discord/guilds/${encodeURIComponent(guildId)}`;

function invalid<T>(): DiscordActionResult<T> {
  return { ok: false, error: INVALID_ARGUMENTS };
}

/** The API's own explanation (the French ProblemDetails detail) rather than its status title. */
function describeError(e: unknown, fallback: string): string {
  if (e instanceof ApiError) {
    const detail = e.problem.detail;
    return typeof detail === "string" && detail.trim() !== "" ? detail : e.message;
  }
  return e instanceof Error && e.message !== "" ? e.message : fallback;
}

/** Calls the API as the signed-in user (an admin when adminOnly) and reports any failure as text. */
async function asUser<T>(
  call: (ctx: ApiCtx) => Promise<T>,
  { adminOnly = false, fallback }: { adminOnly?: boolean; fallback: string },
): Promise<DiscordActionResult<T>> {
  try {
    const session = await getSession();
    if (!session) return { ok: false, error: NOT_SIGNED_IN };
    if (adminOnly && !session.isAdmin) return { ok: false, error: ADMIN_ONLY };
    const data = await call(sessionCtx(session));
    return data === undefined ? { ok: true } : { ok: true, data };
  } catch (e) {
    return { ok: false, error: describeError(e, fallback) };
  }
}

/** The target of a suggestion: the Discord account, the citizen number when known, the RSI handle. */
function parseLinkTarget(discordUserId: unknown, citizenId: unknown, handle: unknown) {
  const user = snowflakeSchema.safeParse(discordUserId);
  const citizen = optionalCitizenIdSchema.safeParse(citizenId);
  const rsiHandle = handleSchema.safeParse(handle);
  if (!user.success || !citizen.success || !rsiHandle.success) return null;
  return { discordUserId: user.data, citizenId: citizen.data, handle: rsiHandle.data };
}

/** Maps a server to an org by SID (the API upper-cases it and refuses an unknown one); null unmaps it. */
export async function mapGuildOrgAction(
  guildId: unknown,
  orgSid: unknown,
): Promise<DiscordActionResult<DiscordGuildSummaryDto>> {
  const guild = snowflakeSchema.safeParse(guildId);
  const sid = orgSidSchema.safeParse(orgSid);
  if (!guild.success || !sid.success) return invalid();
  const path = `${guildPath(guild.data)}/org`;
  const body = { orgSid: sid.data };
  return asUser((ctx) => apiPut<DiscordGuildSummaryDto>(path, body, ctx), {
    fallback: "Échec du rattachement du serveur.",
  });
}

/** Makes a role a rank or not, with its order and the RSI rank it stands for. */
export async function updateGuildRoleAction(
  guildId: unknown,
  roleId: unknown,
  isRank: unknown,
  rankOrder: unknown,
  rsiRankLabel: unknown,
): Promise<DiscordActionResult<DiscordRoleDto>> {
  const guild = snowflakeSchema.safeParse(guildId);
  const role = snowflakeSchema.safeParse(roleId);
  const rank = flagSchema.safeParse(isRank);
  const order = rankOrderSchema.safeParse(rankOrder);
  const label = rsiRankLabelSchema.safeParse(rsiRankLabel);
  if (!guild.success || !role.success || !rank.success || !order.success || !label.success) return invalid();
  const path = `${guildPath(guild.data)}/roles/${encodeURIComponent(role.data)}`;
  const body = { isRank: rank.data, rankOrder: order.data, rsiRankLabel: label.data ? label.data : null };
  return asUser((ctx) => apiPut<DiscordRoleDto>(path, body, ctx), {
    fallback: "Échec de l'enregistrement du rôle.",
  });
}

/** Validates a suggestion: links the Discord account to the citizen. */
export async function acceptSuggestionAction(
  discordUserId: unknown,
  citizenId: unknown,
  handle: unknown,
): Promise<DiscordActionResult<DiscordLinkCreatedDto>> {
  const body = parseLinkTarget(discordUserId, citizenId, handle);
  if (!body) return invalid();
  return asUser((ctx) => apiPost<DiscordLinkCreatedDto>("/api/discord/links", body, ctx), {
    fallback: "Échec de la validation du lien.",
  });
}

/** Ignores a suggestion: it is no longer proposed; the returned id undoes it. */
export async function rejectSuggestionAction(
  discordUserId: unknown,
  citizenId: unknown,
  handle: unknown,
): Promise<DiscordActionResult<DiscordLinkRejectionCreatedDto>> {
  const body = parseLinkTarget(discordUserId, citizenId, handle);
  if (!body) return invalid();
  return asUser((ctx) => apiPost<DiscordLinkRejectionCreatedDto>("/api/discord/link-rejections", body, ctx), {
    fallback: "Échec du rejet de la suggestion.",
  });
}

/** Cancels a rejection (its author or an admin): the suggestion comes back. */
export async function undoRejectionAction(id: unknown): Promise<DiscordActionResult> {
  const parsed = idSchema.safeParse(id);
  if (!parsed.success) return invalid();
  const path = `/api/discord/link-rejections/${parsed.data}`;
  return asUser((ctx) => apiDelete<undefined>(path, ctx), { fallback: "Échec de l'annulation du rejet." });
}

/** Admin: erases a Discord account everywhere and excludes it from later uploads. */
export async function eraseAccountAction(discordUserId: unknown): Promise<DiscordActionResult> {
  const user = snowflakeSchema.safeParse(discordUserId);
  if (!user.success) return invalid();
  const path = `/api/discord/accounts/${encodeURIComponent(user.data)}`;
  return asUser((ctx) => apiDelete<undefined>(path, ctx), {
    adminOnly: true,
    fallback: "Échec de l'effacement du compte.",
  });
}

/** Admin: deletes a server; exclude=true refuses its later uploads, false makes the next one a baseline. */
export async function eraseGuildAction(guildId: unknown, exclude: unknown): Promise<DiscordActionResult> {
  const guild = snowflakeSchema.safeParse(guildId);
  const excluded = flagSchema.safeParse(exclude);
  if (!guild.success || !excluded.success) return invalid();
  const path = guildPath(guild.data);
  const query = { exclude: excluded.data };
  return asUser((ctx) => apiDelete<undefined>(path, { ...ctx, query }), {
    adminOnly: true,
    fallback: "Échec de la suppression du serveur.",
  });
}

/** Admin: lets the next complete upload of this server record more than 25 % departures. */
export async function allowMassDepartureAction(guildId: unknown): Promise<DiscordActionResult> {
  const guild = snowflakeSchema.safeParse(guildId);
  if (!guild.success) return invalid();
  const path = `${guildPath(guild.data)}/allow-mass-departure`;
  return asUser((ctx) => apiPost<undefined>(path, undefined, ctx), {
    adminOnly: true,
    fallback: "Échec de l'autorisation.",
  });
}
```

- [ ] **Step 12: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run "src/app/(public)/discord/actions.test.ts" src/app/action-arguments.test.ts`

Expected: `Test Files  2 passed (2)`. `actions.test.ts`: 21 tests pass. `action-arguments.test.ts`: the 39 new Discord cases pass, and `still passes well-formed arguments through` still sees exactly 2 `apiPost` calls.

- [ ] **Step 13: Write the failing display helper tests**

Create `src/Collector.Web/src/lib/discord/format.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import {
  cleanDiscordText,
  discordDisplayName,
  formatUtc,
  guildIconUrl,
  safeRoleColor,
  syncBadges,
} from "./format";

const GUILD = "123456789012345678";
const HASH = "0123456789abcdef0123456789abcdef";

describe("cleanDiscordText", () => {
  it.each([
    "<script>alert(1)</script>",
    "<img src=x onerror=alert(1)>",
    "**gras** _italique_ [lien](https://evil.example) `code`",
    "@everyone",
  ])("keeps %s as literal text, for React to escape", (name) => {
    expect(cleanDiscordText(name)).toBe(name);
  });

  it("removes the bidi overrides and isolates that would reorder the row", () => {
    expect(cleanDiscordText("\u202Eexe.txt")).toBe("exe.txt");
    expect(cleanDiscordText("a\u2067b\u2069c\u200Fd")).toBe("abcd");
  });

  it("removes zero-width spaces, word joiners and byte order marks", () => {
    expect(cleanDiscordText("Pi\u200Blo\u2060te\uFEFF")).toBe("Pilote");
  });

  it("keeps the zero-width joiners of emoji sequences", () => {
    expect(cleanDiscordText("👩\u200D🚀 Pilote")).toBe("👩\u200D🚀 Pilote");
  });

  it.each([null, undefined, "", "   ", "\u200B\u200D", "\u202E\u200B "])("has nothing to show for %j", (value) => {
    expect(cleanDiscordText(value)).toBeNull();
  });

  it("never shortens a long name: cutting it is left to CSS", () => {
    const long = "Ｗ".repeat(32) + "x".repeat(300);

    expect(cleanDiscordText(long)).toBe(long);
  });
});

describe("discordDisplayName", () => {
  const account = {
    discordUserId: "323456789012345678",
    username: "pilote42",
    globalName: "Pilote",
    nick: "[CORP] Pilote42",
  };

  it("prefers the server nick, then the global name, then the username", () => {
    expect(discordDisplayName(account)).toBe("[CORP] Pilote42");
    expect(discordDisplayName({ ...account, nick: null })).toBe("Pilote");
    expect(discordDisplayName({ ...account, nick: null, globalName: null })).toBe("pilote42");
  });

  it("skips a name with nothing visible in it", () => {
    expect(discordDisplayName({ ...account, nick: "\u200B\u200B" })).toBe("Pilote");
  });

  it("falls back to the account id", () => {
    expect(discordDisplayName({ ...account, nick: null, globalName: null, username: "\u200B" })).toBe(
      "323456789012345678",
    );
  });
});

describe("guildIconUrl", () => {
  it("builds the CDN address from a valid id and hash", () => {
    expect(guildIconUrl(GUILD, HASH)).toBe(`https://cdn.discordapp.com/icons/${GUILD}/${HASH}.png?size=64`);
    expect(guildIconUrl(GUILD, `a_${HASH}`)).toBe(`https://cdn.discordapp.com/icons/${GUILD}/a_${HASH}.png?size=64`);
  });

  it.each([
    [GUILD, null],
    ["../../evil", HASH],
    ["123", HASH],
    [GUILD, "../../../attachments/x"],
    [GUILD, `${HASH}?x=1`],
    [GUILD, HASH.toUpperCase()],
    [GUILD, "javascript:alert(1)"],
  ])("builds no address for server %j and icon %j", (guildId, iconHash) => {
    expect(guildIconUrl(guildId, iconHash)).toBeNull();
  });
});

describe("safeRoleColor", () => {
  it("keeps a #rrggbb colour", () => {
    expect(safeRoleColor("#e67e22")).toBe("#e67e22");
  });

  it.each([null, "#000000", "red", "#fff", "#e67e22;background:url(x)", "expression(alert(1))", "#E67E2G"])(
    "drops %j",
    (color) => {
      expect(safeRoleColor(color)).toBeNull();
    },
  );
});

describe("syncBadges", () => {
  it("marks a complete upload", () => {
    expect(syncBadges({ isComplete: true, departureGuardTripped: false })).toEqual([
      { label: "COMPLET", tone: "green" },
    ]);
  });

  it("marks a partial upload", () => {
    expect(syncBadges({ isComplete: false, departureGuardTripped: false })).toEqual([
      { label: "PARTIEL", tone: "orange" },
    ]);
  });

  it("adds the departure guard, which makes the upload partial", () => {
    expect(syncBadges({ isComplete: false, departureGuardTripped: true })).toEqual([
      { label: "PARTIEL", tone: "orange" },
      { label: "GARDE-FOU", tone: "red" },
    ]);
  });

  it("adds the baseline", () => {
    expect(syncBadges({ isComplete: true, departureGuardTripped: false, isBaseline: true })).toEqual([
      { label: "COMPLET", tone: "green" },
      { label: "BASE", tone: "cyan" },
    ]);
  });
});

describe("formatUtc", () => {
  it.each([
    ["2025-03-14T20:11:05Z", "2025-03-14 20:11 UTC"],
    ["2025-03-14T20:11:05.123+02:00", "2025-03-14 18:11 UTC"],
    ["2026-09-30T12:05:00", "2026-09-30 12:05 UTC"],
    ["2026-09-30", "2026-09-30 00:00 UTC"],
  ])("%s → %s, whatever the machine's time zone", (iso, text) => {
    expect(formatUtc(iso)).toBe(text);
  });

  it.each([null, undefined, "not a date"])("shows a dash for %j", (iso) => {
    expect(formatUtc(iso)).toBe("—");
  });
});
```

- [ ] **Step 14: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/format.test.ts`

Expected: `FAIL  src/lib/discord/format.test.ts` before any test runs, with `Error: Cannot find module './format'` (or Vite's `Failed to load url ./format`).

- [ ] **Step 15: Write the minimal implementation (display helpers)**

Create `src/Collector.Web/src/lib/discord/format.ts`:

```ts
/**
 * Display helpers for the Discord roster pages. Names come from Discord users (server,
 * role, account and nick names): pages render them as React text only, through
 * components/discord/DiscordText, which also cuts long ones with CSS. Nothing here
 * shortens a name.
 */

import { snowflakeSchema } from "@/lib/validation";

/** The tones of HudBadge. */
export type BadgeTone = "cyan" | "orange" | "red" | "green" | "dim";

export interface BadgeSpec {
  label: string;
  tone: BadgeTone;
}

/** Bidi marks, embeddings, overrides and isolates: they reorder the text that follows a name. */
const BIDI_CONTROLS = /[\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]/g;

/** Invisible characters with no use in any script: zero-width space, word joiner, byte order mark. */
const INVISIBLES = /[\u200B\u2060\uFEFF]/g;

/** A name made only of blanks and joiners has nothing to show. */
const NOTHING_VISIBLE = /^[\s\u200C\u200D]*$/;

/**
 * A Discord-supplied string ready to render as text: bidi controls and zero-width spaces
 * removed, ends trimmed, null when nothing visible remains. HTML and markdown are left as
 * typed (React escapes them); emoji joiners are kept.
 */
export function cleanDiscordText(value: string | null | undefined): string | null {
  if (typeof value !== "string") return null;
  const cleaned = value.replace(BIDI_CONTROLS, "").replace(INVISIBLES, "").trim();
  return NOTHING_VISIBLE.test(cleaned) ? null : cleaned;
}

/** How Discord shows an account: server nick, else global name, else username, else its id. */
export function discordDisplayName(account: {
  nick?: string | null;
  globalName: string | null;
  username: string;
  discordUserId?: string;
}): string {
  return (
    cleanDiscordText(account.nick) ??
    cleanDiscordText(account.globalName) ??
    cleanDiscordText(account.username) ??
    account.discordUserId ??
    "—"
  );
}

/** Discord icon hashes: 32 lower-case hex digits, with "a_" first for an animated icon. */
const ICON_HASH = /^(a_)?[0-9a-f]{32}$/;

/**
 * A server icon on Discord's CDN, built only from a valid snowflake and icon hash, each
 * segment encoded: a forged value never yields another address. Null otherwise.
 */
export function guildIconUrl(guildId: string, iconHash: string | null): string | null {
  if (iconHash === null || !ICON_HASH.test(iconHash) || !snowflakeSchema.safeParse(guildId).success) {
    return null;
  }
  return `https://cdn.discordapp.com/icons/${encodeURIComponent(guildId)}/${encodeURIComponent(iconHash)}.png?size=64`;
}

/** A role colour safe in a style attribute: "#rrggbb" only; black is Discord's "no colour". */
export function safeRoleColor(color: string | null): string | null {
  return color !== null && /^#[0-9a-fA-F]{6}$/.test(color) && color !== "#000000" ? color : null;
}

/** Badges of an upload: complete or partial, then the departure guard and the baseline. */
export function syncBadges(sync: {
  isComplete: boolean;
  departureGuardTripped: boolean;
  isBaseline?: boolean;
}): BadgeSpec[] {
  const badges: BadgeSpec[] = [
    sync.isComplete ? { label: "COMPLET", tone: "green" } : { label: "PARTIEL", tone: "orange" },
  ];
  if (sync.departureGuardTripped) badges.push({ label: "GARDE-FOU", tone: "red" });
  if (sync.isBaseline) badges.push({ label: "BASE", tone: "cyan" });
  return badges;
}

const HAS_ZONE = /(?:[zZ]|[+-]\d{2}:?\d{2})$/;

/**
 * "2025-03-14 20:11 UTC": identical on the server and in every browser. A date-time
 * without a zone is read as UTC, the API's time.
 */
export function formatUtc(iso: string | null | undefined): string {
  if (!iso) return "—";
  const date = new Date(iso.includes("T") && !HAS_ZONE.test(iso) ? `${iso}Z` : iso);
  if (Number.isNaN(date.getTime())) return "—";
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())} ` +
    `${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())} UTC`
  );
}
```

- [ ] **Step 16: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/format.test.ts`

Expected: `Test Files  1 passed (1)`, `Tests  44 passed (44)`.

- [ ] **Step 17: Write the failing rendering guard (review focus 1)**

Create `src/Collector.Web/src/lib/discord/rendering-guard.test.ts`:

```ts
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const srcDir = fileURLToPath(new URL("../..", import.meta.url));

/**
 * Everything that renders names typed by Discord users (server, role, account and nick
 * names), relative to src/. A folder is scanned recursively.
 */
const DISCORD_UI = [
  "app/(public)/discord",
  "components/discord",
  "lib/discord",
];

function sourceFiles(path: string): string[] {
  if (statSync(path).isFile()) return [path];
  return readdirSync(path, { withFileTypes: true }).flatMap((entry) => {
    const child = join(path, entry.name);
    if (entry.isDirectory()) return sourceFiles(child);
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.ts$/.test(entry.name) ? [child] : [];
  });
}

describe("Discord names render as inert text", () => {
  const roots = DISCORD_UI.map((path) => join(srcDir, path));

  it("scans paths that exist", () => {
    expect(DISCORD_UI.filter((_, i) => !existsSync(roots[i]!))).toEqual([]);
    expect(roots.filter((root) => existsSync(root)).flatMap(sourceFiles).length).toBeGreaterThan(0);
  });

  it("never injects HTML", () => {
    const offenders = roots
      .filter((root) => existsSync(root))
      .flatMap(sourceFiles)
      .filter((file) =>
        /dangerouslySetInnerHTML|\.innerHTML\b|\.outerHTML\b|insertAdjacentHTML/.test(readFileSync(file, "utf8")),
      );

    expect(offenders.map((file) => file.slice(srcDir.length))).toEqual([]);
  });

  it("cuts long names with CSS and isolates their direction", () => {
    const code = readFileSync(join(srcDir, "components", "discord", "DiscordText.tsx"), "utf8");

    expect(code).toMatch(/<bdi\b/);
    expect(code).toMatch(/\btruncate\b/);
    expect(code).toMatch(/cleanDiscordText\(/);
  });

  it("renders the names Discord users type only through DiscordText", () => {
    // `>{member.nick}` put straight into JSX would skip the cleaning, the <bdi> and the CSS cut.
    const rawName =
      />\s*\{\s*[\w.?!]*\.(?:name|nick|username|globalName|guildName|discordName|matchedToken|discordRank)\s*\}/;
    const offenders = roots
      .filter((root) => existsSync(root))
      .flatMap(sourceFiles)
      .filter((file) => file.endsWith(".tsx") && rawName.test(readFileSync(file, "utf8")));

    expect(offenders.map((file) => file.slice(srcDir.length))).toEqual([]);
  });
});
```

- [ ] **Step 18: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/rendering-guard.test.ts`

Expected: `FAIL`, `Tests  2 failed | 2 passed (4)`. `scans paths that exist` reports `["components/discord"]` instead of `[]`; `cuts long names with CSS…` fails with `ENOENT: no such file or directory` on `components/discord/DiscordText.tsx`; `never injects HTML` and `renders the names Discord users type only through DiscordText` pass (no `.tsx` file exists yet).

- [ ] **Step 19: Write the shared Discord components**

Create `src/Collector.Web/src/components/discord/DiscordText.tsx`:

```tsx
import { cleanDiscordText } from "@/lib/discord/format";
import { cn } from "@/lib/utils/cn";

interface DiscordTextProps {
  /** A name typed by a Discord user (server, role, account or nick). */
  value: string | null | undefined;
  /** Shown when the value is empty or has nothing visible. */
  fallback?: string;
  className?: string;
}

/**
 * A Discord-supplied name as inert text. React escapes it, so HTML and markdown stay
 * literal; bidi controls and zero-width spaces are removed; <bdi> keeps its direction
 * from reordering the row around it; CSS truncates it with an ellipsis, so a very long
 * name never widens a table. The whole value stays in the tooltip.
 */
export function DiscordText({ value, fallback = "—", className }: DiscordTextProps) {
  const text = cleanDiscordText(value) ?? fallback;
  return (
    <bdi title={text} className={cn("inline-block max-w-full truncate align-bottom", className)}>
      {text}
    </bdi>
  );
}
```

Create `src/Collector.Web/src/components/discord/GuildIcon.tsx`:

```tsx
import { guildIconUrl } from "@/lib/discord/format";
import { cn } from "@/lib/utils/cn";

interface GuildIconProps {
  guildId: string;
  iconHash: string | null;
  /** Width and height in pixels. */
  size?: number;
  className?: string;
}

/** A server icon from Discord's CDN, or an empty square when the server has none (or a bad hash). */
export function GuildIcon({ guildId, iconHash, size = 32, className }: GuildIconProps) {
  const src = guildIconUrl(guildId, iconHash);
  if (!src) {
    return (
      <span
        aria-hidden
        className={cn("inline-block shrink-0 rounded-full border border-hud-cyan/30 bg-hud-bg/60", className)}
        style={{ width: size, height: size }}
      />
    );
  }
  return (
    // eslint-disable-next-line @next/next/no-img-element -- Discord CDN, no /_next/image optimizer (next.config.ts)
    <img
      src={src}
      alt=""
      width={size}
      height={size}
      loading="lazy"
      referrerPolicy="no-referrer"
      className={cn("shrink-0 rounded-full border border-hud-cyan/30", className)}
    />
  );
}
```

Create `src/Collector.Web/src/components/discord/RoleDot.tsx`:

```tsx
import { safeRoleColor } from "@/lib/discord/format";

/** The colour of a Discord role, as a dot; hollow for a role without colour. */
export function RoleDot({ color }: { color: string | null }) {
  const safe = safeRoleColor(color);
  return (
    <span
      aria-hidden
      className="inline-block h-2 w-2 shrink-0 rounded-full border border-hud-text-dim/40"
      style={safe ? { backgroundColor: safe, borderColor: safe } : undefined}
    />
  );
}
```

- [ ] **Step 20: Write the org mapping form, the servers table and the /discord page**

Create `src/Collector.Web/src/app/(public)/discord/GuildOrgForm.tsx`:

```tsx
"use client";
import { useEffect, useId, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { searchOrgsAction, type OrgOption } from "@/app/(public)/users/[handle]/membership-actions";
import { sidSchema } from "@/lib/validation";
import { mapGuildOrgAction } from "./actions";

interface GuildOrgFormProps {
  guildId: string;
  /** SID the server is mapped to; null while unmapped. */
  currentSid: string | null;
  /** False once mapped, for anyone but the responsible user and admins. */
  canEdit: boolean;
}

/** Known orgs are proposed once this many characters are typed. */
const MIN_SEARCH_LENGTH = 2;

/**
 * Maps a Discord server to the RSI org it stands for. Typing proposes the orgs the
 * tracker knows (searchOrgsAction, by SID or name: spec § 12 « recherche d'org
 * existante »). The SID is checked here (sidSchema) for a quick answer, again by the
 * server action, and the API refuses an org it does not know; each answer is a toast.
 */
export function GuildOrgForm(props: GuildOrgFormProps) {
  // Keyed by the saved SID: after router.refresh() the field starts again from what the API stored.
  return <GuildOrgFields key={props.currentSid ?? "unmapped"} {...props} />;
}

function GuildOrgFields({ guildId, currentSid, canEdit }: GuildOrgFormProps) {
  const router = useRouter();
  const listId = useId();
  const [sid, setSid] = useState(currentSid ?? "");
  const [options, setOptions] = useState<OrgOption[]>([]);
  const [busy, setBusy] = useState(false);

  // Known orgs matching what is typed, debounced; a late answer never replaces a newer one.
  useEffect(() => {
    const query = sid.trim();
    if (!canEdit || query.length < MIN_SEARCH_LENGTH || query.toUpperCase() === currentSid) {
      setOptions([]);
      return;
    }
    let stale = false;
    const timer = setTimeout(async () => {
      const found = await searchOrgsAction(query);
      if (!stale) setOptions(found);
    }, 300);
    return () => {
      stale = true;
      clearTimeout(timer);
    };
  }, [sid, canEdit, currentSid]);

  async function save(orgSid: string | null) {
    setBusy(true);
    const res = await mapGuildOrgAction(guildId, orgSid);
    setBusy(false);
    if (res.ok) {
      toast.success(res.data?.orgSid ? `Serveur relié à ${res.data.orgSid}.` : "Serveur délié de sa corpo.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const parsed = sidSchema.safeParse(sid);
    if (!parsed.success) {
      toast.error("SID invalide : 1 à 10 caractères parmi lettres, chiffres, _ et -.");
      return;
    }
    await save(parsed.data.toUpperCase());
  }

  function unmap() {
    if (currentSid && confirm(`Délier ce serveur de ${currentSid} ?`)) void save(null);
  }

  return (
    <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
      <HudInput
        label="SID DE LA CORPO"
        name="orgSid"
        type="text"
        required
        maxLength={10}
        autoComplete="off"
        list={listId}
        placeholder="SID ou nom"
        value={sid}
        onChange={(e) => setSid(e.target.value)}
        disabled={!canEdit || busy}
        className="w-40 uppercase"
      />
      {/* RSI org names, set as attributes: the browser lists them next to each SID. */}
      <datalist id={listId}>
        {options.map((org) => (
          <option key={org.sid} value={org.sid} label={org.name} />
        ))}
      </datalist>
      <HudButton type="submit" disabled={!canEdit || busy}>
        {busy ? "…" : currentSid ? "CHANGER" : "RELIER"}
      </HudButton>
      {currentSid && (
        <HudButton type="button" variant="ghost" disabled={!canEdit || busy} onClick={unmap}>
          DÉLIER
        </HudButton>
      )}
    </form>
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/DiscordGuildsTable.tsx`:

```tsx
"use client";
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { RoleDot } from "@/components/discord/RoleDot";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordGuildSummaryDto } from "@/lib/api/types";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { formatNumber } from "@/lib/utils/format";

/** Ranks listed per server; the others are counted. */
const TOP_RANKS = 3;

function RankSummary({ ranks }: { ranks: DiscordGuildSummaryDto["rankDistribution"] }) {
  if (ranks.length === 0) return <span className="text-hud-text-dim">—</span>;
  return (
    <span className="flex min-w-0 flex-col gap-0.5">
      {ranks.slice(0, TOP_RANKS).map((r) => (
        <span key={r.roleId} className="flex min-w-0 items-center gap-1">
          <RoleDot color={r.color} />
          <DiscordText value={r.name} className="min-w-0" />
          <span className="shrink-0 tabular-nums text-hud-text-dim">×{formatNumber(r.count)}</span>
        </span>
      ))}
      {ranks.length > TOP_RANKS && (
        <span className="text-[10px] text-hud-text-dim">+{ranks.length - TOP_RANKS} autres rangs</span>
      )}
    </span>
  );
}

function LastSync({ sync }: { sync: DiscordGuildSummaryDto["lastSync"] }) {
  if (!sync) return <span className="text-hud-text-dim">—</span>;
  return (
    <span className="flex flex-col gap-1">
      <span className="flex flex-wrap gap-1">
        {syncBadges(sync).map((b) => (
          <HudBadge key={b.label} tone={b.tone}>
            {b.label}
          </HudBadge>
        ))}
      </span>
      <span className="text-[10px] text-hud-text-dim">
        {formatUtc(sync.receivedAt)} · {sync.submittedBy}
      </span>
    </span>
  );
}

/** Tracked servers: org, active members, main ranks and last upload. Unmapped ones come first (API order). */
export function DiscordGuildsTable({ rows }: { rows: DiscordGuildSummaryDto[] }) {
  const columns: HudColumn<DiscordGuildSummaryDto>[] = [
    {
      key: "icon",
      header: "",
      width: "w-14",
      render: (g) => <GuildIcon guildId={g.guildId} iconHash={g.iconHash} />,
    },
    {
      key: "name",
      header: "SERVEUR",
      width: "min-w-0 flex-1",
      sortable: true,
      sortValue: (g) => g.name.toLowerCase(),
      render: (g) => (
        <Link
          href={`/discord/${encodeURIComponent(g.guildId)}`}
          className="inline-flex min-w-0 max-w-full text-hud-cyan hover:text-hud-orange"
        >
          <DiscordText value={g.name} />
        </Link>
      ),
    },
    {
      key: "org",
      header: "CORPO",
      width: "w-36 min-w-0",
      sortable: true,
      sortValue: (g) => g.orgSid ?? "",
      render: (g) =>
        g.orgSid ? (
          <Link
            href={`/orgs/${encodeURIComponent(g.orgSid)}`}
            title={g.orgName ?? g.orgSid}
            className="block truncate text-hud-cyan hover:text-hud-orange"
          >
            {g.orgSid}
          </Link>
        ) : (
          <HudBadge tone="orange">NON RELIÉ</HudBadge>
        ),
    },
    {
      key: "active",
      header: "ACTIFS",
      width: "w-20",
      align: "right",
      sortable: true,
      sortValue: (g) => g.activeMembers,
      render: (g) => formatNumber(g.activeMembers),
    },
    {
      key: "ranks",
      header: "RANGS",
      width: "w-56 min-w-0",
      render: (g) => <RankSummary ranks={g.rankDistribution} />,
    },
    {
      key: "lastSync",
      header: "DERNIER ENVOI",
      width: "w-60",
      sortable: true,
      sortValue: (g) => g.lastSync?.receivedAt ?? null,
      render: (g) => <LastSync sync={g.lastSync} />,
    },
  ];

  return <HudDataGrid columns={columns} rows={rows} rowKey={(g) => g.guildId} empty="Aucun serveur." />;
}
```

Create `src/Collector.Web/src/app/(public)/discord/page.tsx`:

```tsx
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { HudPanel } from "@/components/hud/HudPanel";
import { listDiscordGuilds } from "@/lib/api/endpoints";
import { requireAuthCtx, withAuthRedirect } from "@/lib/auth/server-api";
import { formatNumber } from "@/lib/utils/format";
import { DiscordGuildsTable } from "./DiscordGuildsTable";
import { GuildOrgForm } from "./GuildOrgForm";

export const dynamic = "force-dynamic";

/** Servers uploaded by the Vencord plugin; the ones not mapped to an org yet come first, with their SID form. */
export default async function DiscordPage() {
  const ctx = await requireAuthCtx();
  const guilds = await withAuthRedirect(listDiscordGuilds(ctx));
  const unmapped = guilds.filter((g) => g.orgSid === null);

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <div className="hud-label">— UEE::DISCORD_ROSTERS</div>
          <h1 className="mt-1 font-display text-3xl">Serveurs Discord</h1>
          <p className="mt-1 font-mono text-xs text-hud-text-dim">
            Membres envoyés par le plugin Vencord, serveur par serveur, à la demande.
          </p>
        </div>
        <Link
          href="/discord/multi"
          className="hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10"
        >
          MULTI-APPARTENANCE
        </Link>
      </header>

      {guilds.length === 0 ? (
        <HudPanel label="SERVEURS DISCORD">
          <p className="py-6 text-center font-mono text-xs text-hud-text-dim">
            Aucun serveur reçu. Installe le plugin :{" "}
            <Link href="/settings" className="text-hud-cyan hover:text-hud-orange">
              {"Paramètres → Clé d'envoi Discord"}
            </Link>
          </p>
        </HudPanel>
      ) : (
        <>
          {unmapped.length > 0 && (
            <HudPanel label={`SERVEURS NON RELIÉS · ${unmapped.length}`} accent="orange">
              <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
                {"Relie chaque serveur à la corpo RSI qu'il représente : ses rôles deviennent des rangs et ses membres sont recoupés avec le roster RSI."}
              </p>
              <ul className="flex flex-col divide-y divide-hud-cyan/10">
                {unmapped.map((g) => (
                  <li key={g.guildId} className="flex flex-wrap items-center justify-between gap-3 py-3">
                    <Link
                      href={`/discord/${encodeURIComponent(g.guildId)}`}
                      className="flex min-w-0 flex-1 items-center gap-3 font-mono text-sm text-hud-cyan hover:text-hud-orange"
                    >
                      <GuildIcon guildId={g.guildId} iconHash={g.iconHash} />
                      <DiscordText value={g.name} className="min-w-0" />
                      <span className="shrink-0 text-[10px] text-hud-text-dim">
                        {formatNumber(g.activeMembers)} actifs
                      </span>
                    </Link>
                    <GuildOrgForm guildId={g.guildId} currentSid={null} canEdit />
                  </li>
                ))}
              </ul>
            </HudPanel>
          )}

          <HudPanel label={`${formatNumber(guilds.length)} SERVEURS SUIVIS`}>
            <DiscordGuildsTable rows={guilds} />
          </HudPanel>
        </>
      )}
    </div>
  );
}
```

- [ ] **Step 21: Add the DISCORD tab to the navigation**

In `src/Collector.Web/src/components/layout/TopNav.tsx`, replace:

```ts
const NAV_ITEMS = [
  { href: "/orgs", label: "ORGS" },
  { href: "/users", label: "USERS" },
  { href: "/stats", label: "STATS" },
  { href: "/changes", label: "CHANGELOG" },
];
```

with:

```ts
const NAV_ITEMS = [
  { href: "/orgs", label: "ORGS" },
  { href: "/users", label: "USERS" },
  { href: "/discord", label: "DISCORD" },
  { href: "/stats", label: "STATS" },
  { href: "/changes", label: "CHANGELOG" },
];
```

- [ ] **Step 22: Run the guard, the typecheck, the whole suite and the build**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/rendering-guard.test.ts`

Expected: `Test Files  1 passed (1)`, `Tests  4 passed (4)`.

Run (from `src/Collector.Web`): `pnpm typecheck`

Expected: exits 0 with no `error TS` line.

Run (from `src/Collector.Web`): `pnpm test`

Expected: every test file passes (no `FAIL` line); `src/lib/api/server-boundary.test.ts` passes: `GuildOrgForm` and `DiscordGuildsTable` import only types, `@/lib/discord/format`, `@/lib/validation` and the server actions, never `@/lib/api/client` or `@/lib/api/endpoints`.

Run (from `src/Collector.Web`): `pnpm build`

Expected: `✓ Compiled successfully`, exit code 0, and the route table lists `ƒ /discord`.

- [ ] **Step 23: Commit**

Run from the repository root:

```bash
git add src/Collector.Web/src/lib/validation.ts \
  src/Collector.Web/src/lib/validation.test.ts \
  src/Collector.Web/src/lib/api/types.ts \
  src/Collector.Web/src/lib/api/endpoints.ts \
  src/Collector.Web/src/lib/api/endpoints.test.ts \
  src/Collector.Web/src/app/action-arguments.test.ts \
  src/Collector.Web/src/lib/discord \
  src/Collector.Web/src/components/discord \
  "src/Collector.Web/src/app/(public)/discord" \
  src/Collector.Web/src/components/layout/TopNav.tsx
git commit -F - <<'EOF'
feat(web): list tracked Discord servers and map them to a corpo

The DISCORD tab lists the servers uploaded by the Vencord plugin: org, active
members, main ranks and last upload, with the unmapped servers first and a SID
form to map each one. The data layer carries every lot C read (types, endpoints
with encoded path ids) and the server actions for mapping, ranks, suggestions
and the admin erasures; each action validates every argument with zod before
touching the session or the API, and returns its outcome instead of throwing.

Discord names are typed by strangers: they only ever render as React text,
cleaned of bidi controls and zero-width spaces, isolated in <bdi> and cut by
CSS. A source-scan guard fails the build of any Discord UI file that injects
HTML, and the server icon URL is built only from a validated id and hash.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task C9: /discord/[guildId] tabs

**Files:**
- Create: `src/Collector.Web/src/lib/discord/params.ts`
- Modify: `src/Collector.Web/src/lib/discord/format.ts` (imports; new helpers appended after `formatUtc`)
- Create: `src/Collector.Web/src/components/discord/RoleChanges.tsx`
- Create: `src/Collector.Web/src/components/discord/ValueChange.tsx`
- Create: `src/Collector.Web/src/components/discord/LinkedPeople.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/page.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/load.ts`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/GuildTabNav.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/UnmappedNotice.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/GuildAdminActions.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/MembersTab.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/MemberFilterForm.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/DiscordMembersTable.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/HistoryTab.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/GapsTab.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/DiscrepanciesTable.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/SuggestionsTab.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/SuggestionsList.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/ConfigTab.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/RoleConfigTable.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/SyncsTab.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/[guildId]/SyncsTable.tsx`
- Test: `src/Collector.Web/src/lib/discord/params.test.ts`
- Test: `src/Collector.Web/src/lib/discord/format.test.ts` (import list; new `describe` blocks at the end)
- Test: `src/Collector.Web/src/lib/discord/rendering-guard.test.ts` (unchanged: it already scans `app/(public)/discord` and `components/discord`, so every file of this task is covered)

**Interfaces:**
- Consumes:
  - Task C8: types `DiscordGuildDetailDto`, `DiscordRoleDto`, `DiscordMemberDto`, `DiscordEventDto`, `DiscordSyncDto`, `DiscordDiscrepancyDto`, `DiscordDiscrepancyKind`, `DiscordSuggestionDto`, `DiscordSuggestionConfidence`, `DiscordReconciliation`, `DiscordLinkedPersonDto`; endpoints `getDiscordGuild(ctx, guildId)`, `getDiscordMembers(ctx, guildId, q)`, `getDiscordEvents(ctx, guildId, q)`, `getDiscordSyncs(ctx, guildId, limit)`, `getDiscordDiscrepancies(ctx, guildId)`, `getDiscordSuggestions(ctx, guildId)`; actions `mapGuildOrgAction`, `updateGuildRoleAction`, `acceptSuggestionAction`, `rejectSuggestionAction`, `undoRejectionAction`, `eraseAccountAction`, `eraseGuildAction`, `allowMassDepartureAction` (all `{ ok, data?, error? }`); `snowflakeSchema`, `valid`; format helpers `BadgeTone`, `BadgeSpec`, `cleanDiscordText`, `discordDisplayName`, `formatUtc`, `syncBadges`; components `DiscordText`, `GuildIcon`, `RoleDot`, `GuildOrgForm`.
  - CONTRACTS § 7 semantics: `members` default status `active`, `reconciliation` ignored for an unmapped server (not sent here either); `events` newest Id first, `limit` default 100; `discrepancies` of an unmapped server = `{ orgSid: null, items: [], totals: null }`; `rsiOnlyAvailable` false without a complete upload; `rankChange` set when a `roles_changed` changes the rank; `canEdit` from the responsible-user rule (C6).
  - Existing: `requireAuthCtx`, `withAuthRedirect`, `AuthCtx` (`@/lib/auth/server-api`), `getSession`, `ApiError`, `notFound` (`next/navigation`), `Pagination` (`param` prop), `parsePage`, `HudPanel`, `HudBadge`, `HudButton`, `HudStatTile`, `HudDataGrid`, `formatNumber`, `cn`, `toast` (sonner 1.7: `toast.success(message, { action: { label, onClick } })`).
- Produces:
  - `src/lib/discord/params.ts`: `SearchParams`, `DISCORD_TABS`, `DiscordTab`, `TAB_LABELS`, `MEMBER_STATUSES`, `MemberStatus`, `RECONCILIATIONS`, `EVENT_TYPES`, `DiscordEventType`, `MEMBERS_PAGE_SIZE = 50`, `HISTORY_LIMIT = 100`, `SYNCS_LIMIT = 50`, `MAX_SEARCH_LENGTH = 100`, `firstParam(value)`, `parseDiscordTab(raw)`, `MemberFilters` + `parseMemberFilters(sp)`, `EventFilters` + `parseEventFilters(sp)`, `tabHref(guildId, tab, extra?)`, `parseRankOrderInput(raw)`.
  - `format.ts` additions: `reconciliationBadge`, `eventTypeLabel`, `eventTone` (Discord types and the RSI `member_joined` / `member_left`), `eventWhen`, `rankChangeText`, `RoleRef`, `parseRoleList`, `rolesDiff`, `discrepancyKindBadge`, `confidenceBadge`, `formatShare`.
  - Components: `RoleChanges({ oldValue, newValue })`, `ValueChange({ oldValue, newValue })`, `LinkedPeople({ links, multiple? })` in `src/components/discord/` (C10 reuses them).
  - Page `/discord/[guildId]?tab=members|history|gaps|suggestions|config|syncs` (default `members`); a non-snowflake id or an API 404 gives the not-found page; `loadOrNotFound(promise)` in `[guildId]/load.ts`.

- [ ] **Step 1: Write the failing URL parameter tests**

Create `src/Collector.Web/src/lib/discord/params.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import {
  parseDiscordTab,
  parseEventFilters,
  parseMemberFilters,
  parseRankOrderInput,
  tabHref,
} from "./params";

const GUILD = "123456789012345678";
const ROLE = "223456789012345678";
const USER = "323456789012345678";

describe("parseDiscordTab", () => {
  it.each([
    [undefined, "members"],
    ["members", "members"],
    ["history", "history"],
    ["gaps", "gaps"],
    ["suggestions", "suggestions"],
    ["config", "config"],
    ["syncs", "syncs"],
    [["syncs", "config"], "syncs"],
    ["HISTORY", "members"],
    ["__proto__", "members"],
    ["", "members"],
  ])("%j → %s", (raw, tab) => {
    expect(parseDiscordTab(raw)).toBe(tab);
  });
});

describe("parseMemberFilters", () => {
  it("defaults to the members present, first page", () => {
    expect(parseMemberFilters({})).toEqual({ status: "active", page: 1 });
  });

  it("reads every filter of the members tab", () => {
    expect(
      parseMemberFilters({
        status: "former",
        search: "  pilote ",
        rankRoleId: ROLE,
        reconciliation: "rank_mismatch",
        page: "3",
      }),
    ).toEqual({ status: "former", search: "pilote", rankRoleId: ROLE, reconciliation: "rank_mismatch", page: 3 });
  });

  it("drops what the API would not understand", () => {
    expect(
      parseMemberFilters({ status: "banned", search: "   ", rankRoleId: "../1", reconciliation: "evil", page: "-2" }),
    ).toEqual({ status: "active", page: 1 });
  });

  it("ignores a search longer than 100 characters", () => {
    expect(parseMemberFilters({ search: "x".repeat(101) }).search).toBeUndefined();
    expect(parseMemberFilters({ search: "x".repeat(100) }).search).toBe("x".repeat(100));
  });

  it("keeps the first of a repeated parameter", () => {
    expect(parseMemberFilters({ status: ["all", "former"], page: ["2", "9"] })).toEqual({ status: "all", page: 2 });
  });
});

describe("parseEventFilters", () => {
  it("reads the event type and the account", () => {
    expect(parseEventFilters({ type: "roles_changed", userId: USER })).toEqual({ type: "roles_changed", userId: USER });
  });

  it("drops an unknown type and an account id that is not a snowflake", () => {
    expect(parseEventFilters({ type: "boost", userId: "../admin" })).toEqual({});
  });
});

describe("tabHref", () => {
  it("links to a tab, the members tab being the page itself", () => {
    expect(tabHref(GUILD, "members")).toBe(`/discord/${GUILD}`);
    expect(tabHref(GUILD, "config")).toBe(`/discord/${GUILD}?tab=config`);
    expect(tabHref(GUILD, "history", { userId: USER })).toBe(`/discord/${GUILD}?tab=history&userId=${USER}`);
  });

  it("encodes the server id", () => {
    expect(tabHref("1/2", "syncs")).toBe("/discord/1%2F2?tab=syncs");
  });
});

describe("parseRankOrderInput", () => {
  it.each([
    ["", null],
    ["  ", null],
    ["0", 0],
    [" 12 ", 12],
    ["1000", 1000],
  ])("accepts %j as %j", (raw, value) => {
    expect(parseRankOrderInput(raw)).toEqual({ ok: true, value });
  });

  it.each(["1001", "-1", "1.5", "abc", "1e3", "12345"])("refuses %j", (raw) => {
    expect(parseRankOrderInput(raw)).toEqual({ ok: false });
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/params.test.ts`

Expected: `FAIL  src/lib/discord/params.test.ts` before any test runs, with `Error: Cannot find module './params'` (or Vite's `Failed to load url ./params`).

- [ ] **Step 3: Write the minimal implementation (URL parameters)**

Create `src/Collector.Web/src/lib/discord/params.ts`:

```ts
/**
 * URL parameters of the Discord server page. They come from the address bar, possibly
 * repeated or forged: unknown values fall back to defaults and never reach the API.
 */

import type { DiscordReconciliation } from "@/lib/api/types";
import { parsePage } from "@/lib/utils/page-param";
import { snowflakeSchema } from "@/lib/validation";

/** Next.js search params, as a page receives them. */
export type SearchParams = Record<string, string | string[] | undefined>;

export const DISCORD_TABS = ["members", "history", "gaps", "suggestions", "config", "syncs"] as const;
export type DiscordTab = (typeof DISCORD_TABS)[number];

export const TAB_LABELS: Record<DiscordTab, string> = {
  members: "MEMBRES",
  history: "HISTORIQUE",
  gaps: "ÉCARTS RSI",
  suggestions: "SUGGESTIONS",
  config: "CONFIG",
  syncs: "ENVOIS",
};

export const MEMBER_STATUSES = ["active", "former", "all"] as const;
export type MemberStatus = (typeof MEMBER_STATUSES)[number];

export const RECONCILIATIONS: readonly DiscordReconciliation[] = [
  "ok",
  "unlinked",
  "rank_mismatch",
  "not_in_rsi_org",
  "rsi_unknown",
];

export const EVENT_TYPES = [
  "joined",
  "left",
  "rejoined",
  "roles_changed",
  "nick_changed",
  "username_changed",
  "global_name_changed",
] as const;
export type DiscordEventType = (typeof EVENT_TYPES)[number];

/** Members per page, paged by the API (a server can have 50 000). */
export const MEMBERS_PAGE_SIZE = 50;
/** Events shown in the history tab. */
export const HISTORY_LIMIT = 100;
/** Uploads shown in the journal tab. */
export const SYNCS_LIMIT = 50;
/** Longest member search sent to the API. */
export const MAX_SEARCH_LENGTH = 100;

/** The first value of a search param: a repeated param keeps its first occurrence. */
export function firstParam(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}

function oneOf<T extends string>(values: readonly T[], raw: string | undefined): T | undefined {
  return values.find((value) => value === raw);
}

/** The tab asked for by ?tab=, members by default. */
export function parseDiscordTab(raw: string | string[] | undefined): DiscordTab {
  return oneOf(DISCORD_TABS, firstParam(raw)) ?? "members";
}

export interface MemberFilters {
  status: MemberStatus;
  search?: string;
  rankRoleId?: string;
  reconciliation?: DiscordReconciliation;
  page: number;
}

/** Filters of the members tab: status, search, rank role, reconciliation status and page. */
export function parseMemberFilters(sp: SearchParams): MemberFilters {
  const search = firstParam(sp.search)?.trim() ?? "";
  const rankRoleId = firstParam(sp.rankRoleId) ?? "";
  return {
    status: oneOf(MEMBER_STATUSES, firstParam(sp.status)) ?? "active",
    search: search !== "" && search.length <= MAX_SEARCH_LENGTH ? search : undefined,
    rankRoleId: snowflakeSchema.safeParse(rankRoleId).success ? rankRoleId : undefined,
    reconciliation: oneOf(RECONCILIATIONS, firstParam(sp.reconciliation)),
    page: parsePage(firstParam(sp.page)),
  };
}

export interface EventFilters {
  type?: DiscordEventType;
  userId?: string;
}

/** Filters of the history tab: event type and account. */
export function parseEventFilters(sp: SearchParams): EventFilters {
  const userId = firstParam(sp.userId) ?? "";
  return {
    type: oneOf(EVENT_TYPES, firstParam(sp.type)),
    userId: snowflakeSchema.safeParse(userId).success ? userId : undefined,
  };
}

/** Link to a tab of a server page with the tab's own parameters; members is the page itself. */
export function tabHref(guildId: string, tab: DiscordTab, extra: Record<string, string> = {}): string {
  const query = new URLSearchParams();
  if (tab !== "members") query.set("tab", tab);
  for (const [key, value] of Object.entries(extra)) query.set(key, value);
  const qs = query.toString();
  return `/discord/${encodeURIComponent(guildId)}${qs ? `?${qs}` : ""}`;
}

/** A rank order typed in the config tab: blank is null (the API keeps or derives it), else a whole number from 0 to 1000. */
export function parseRankOrderInput(raw: string): { ok: true; value: number | null } | { ok: false } {
  const text = raw.trim();
  if (text === "") return { ok: true, value: null };
  if (!/^\d{1,4}$/.test(text)) return { ok: false };
  const value = Number(text);
  return value <= 1000 ? { ok: true, value } : { ok: false };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/params.test.ts`

Expected: `Test Files  1 passed (1)`, `Tests  31 passed (31)`.

- [ ] **Step 5: Write the failing label and history helper tests**

In `src/Collector.Web/src/lib/discord/format.test.ts`, replace:

```ts
import { describe, expect, it } from "vitest";
import {
  cleanDiscordText,
  discordDisplayName,
  formatUtc,
  guildIconUrl,
  safeRoleColor,
  syncBadges,
} from "./format";
```

with:

```ts
import { describe, expect, it } from "vitest";
import type { DiscordReconciliation } from "@/lib/api/types";
import {
  cleanDiscordText,
  confidenceBadge,
  discordDisplayName,
  discrepancyKindBadge,
  eventTone,
  eventTypeLabel,
  eventWhen,
  formatShare,
  formatUtc,
  guildIconUrl,
  parseRoleList,
  rankChangeText,
  reconciliationBadge,
  rolesDiff,
  safeRoleColor,
  syncBadges,
} from "./format";
```

Then append at the end of the file:

```ts

describe("reconciliationBadge", () => {
  it.each([
    ["ok", "OK", "green"],
    ["unlinked", "non lié", "dim"],
    ["rank_mismatch", "rang différent", "orange"],
    ["not_in_rsi_org", "absent de l'org RSI ou caché sur RSI", "red"],
    ["rsi_unknown", "roster RSI jamais lu", "dim"],
  ] as const)("labels %s in French", (status, label, tone) => {
    expect(reconciliationBadge(status)).toEqual({ label, tone });
  });

  it("has no badge for a bot or an unmapped server", () => {
    expect(reconciliationBadge(null)).toBeNull();
  });

  it("shows a status it does not know as it is", () => {
    expect(reconciliationBadge("new_status" as DiscordReconciliation)).toEqual({ label: "new_status", tone: "dim" });
  });
});

describe("eventTypeLabel", () => {
  it.each([
    ["joined", "arrivée"],
    ["left", "départ"],
    ["rejoined", "retour"],
    ["roles_changed", "rôles"],
    ["nick_changed", "pseudo"],
    ["username_changed", "nom d'utilisateur"],
    ["global_name_changed", "nom affiché"],
  ])("labels %s as « %s »", (type, label) => {
    expect(eventTypeLabel(type)).toBe(label);
  });

  it("spells out an unknown type, Object.prototype names included", () => {
    expect(eventTypeLabel("boost_changed")).toBe("boost changed");
    expect(eventTypeLabel("constructor")).toBe("constructor");
  });
});

describe("eventTone", () => {
  it.each([
    ["joined", "green"],
    ["member_joined", "green"],
    ["left", "red"],
    ["member_left", "red"],
    ["rejoined", "orange"],
    ["roles_changed", "orange"],
    ["nick_changed", "orange"],
  ])("%s is %s", (type, tone) => {
    expect(eventTone(type)).toBe(tone);
  });
});

describe("eventWhen", () => {
  const observedAt = "2026-09-30T12:00:00Z";

  it("gives the exact date when it is known", () => {
    expect(eventWhen({ occurredAt: "2026-09-29T08:30:00Z", notBefore: "2026-09-01T00:00:00Z", observedAt })).toBe(
      "2026-09-29 08:30 UTC",
    );
  });

  it("gives the window between the previous upload and this one otherwise", () => {
    expect(eventWhen({ occurredAt: null, notBefore: "2026-09-28T20:00:00Z", observedAt })).toBe(
      "entre 2026-09-28 20:00 UTC et 2026-09-30 12:00 UTC",
    );
  });

  it("gives the upload date as an upper bound without a previous upload", () => {
    expect(eventWhen({ occurredAt: null, notBefore: null, observedAt })).toBe("au plus tard 2026-09-30 12:00 UTC");
  });
});

describe("rankChangeText", () => {
  it.each([
    [{ from: "Recrue", to: "Officier" }, "Rang : Recrue → Officier"],
    [{ from: null, to: "Officier" }, "Rang : aucun → Officier"],
    [{ from: "\u202Eerucer", to: null }, "Rang : erucer → aucun"],
  ])("%j → %s", (change, text) => {
    expect(rankChangeText(change)).toBe(text);
  });
});

describe("parseRoleList", () => {
  it("reads the roles of a roles_changed value, with their names of the time", () => {
    expect(parseRoleList('[{"id":"1","name":"Officier"},{"id":"2","name":"<b>Pilote</b>"}]')).toEqual([
      { id: "1", name: "Officier" },
      { id: "2", name: "<b>Pilote</b>" },
    ]);
  });

  it.each([null, "", "not json", '{"id":"1"}', "[1,2]"])("reads nothing from %j", (json) => {
    expect(parseRoleList(json)).toEqual([]);
  });

  it("drops malformed entries and extra fields", () => {
    expect(parseRoleList('[{"id":"1","name":"A","color":"#fff"},{"id":2,"name":"B"},{"name":"C"},null]')).toEqual([
      { id: "1", name: "A" },
    ]);
  });
});

describe("rolesDiff", () => {
  it("compares roles by id", () => {
    const before = '[{"id":"1","name":"Recrue"},{"id":"2","name":"Pilote"}]';
    const after = '[{"id":"2","name":"Pilote renommé"},{"id":"3","name":"Officier"}]';

    expect(rolesDiff(before, after)).toEqual({
      added: [{ id: "3", name: "Officier" }],
      removed: [{ id: "1", name: "Recrue" }],
    });
  });
});

describe("discrepancyKindBadge", () => {
  it.each([
    ["rsi_only", "sur RSI seulement", "orange"],
    ["not_in_rsi_org", "absent de l'org RSI ou caché sur RSI", "red"],
    ["rank_mismatch", "rang différent", "orange"],
  ] as const)("labels %s in French", (kind, label, tone) => {
    expect(discrepancyKindBadge(kind)).toEqual({ label, tone });
  });
});

describe("confidenceBadge", () => {
  it.each([
    ["strong", "confiance forte", "green"],
    ["medium", "confiance moyenne", "orange"],
  ] as const)("labels %s in French", (confidence, label, tone) => {
    expect(confidenceBadge(confidence)).toEqual({ label, tone });
  });
});

describe("formatShare", () => {
  it.each([
    [3, 4, "75 %"],
    [1, 3, "33 %"],
    [0, 10, "0 %"],
    [0, 0, "—"],
  ])("%d of %d → %s", (part, whole, text) => {
    expect(formatShare(part, whole)).toBe(text);
  });
});
```

- [ ] **Step 6: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/format.test.ts`

Expected: `FAIL  src/lib/discord/format.test.ts`; the 45 new tests fail with `TypeError: reconciliationBadge is not a function` (or the name of the missing helper each one calls); the 44 tests of C8 still pass.

- [ ] **Step 7: Write the minimal implementation (labels and history helpers)**

In `src/Collector.Web/src/lib/discord/format.ts`, replace:

```ts
import { snowflakeSchema } from "@/lib/validation";
```

with:

```ts
import type {
  DiscordDiscrepancyKind,
  DiscordReconciliation,
  DiscordSuggestionConfidence,
} from "@/lib/api/types";
import { snowflakeSchema } from "@/lib/validation";
```

Append at the end of `src/Collector.Web/src/lib/discord/format.ts`:

```ts

// Lookups go through Map: a Record would answer "constructor" with a function.
const RECONCILIATION_BADGES = new Map<string, BadgeSpec>([
  ["ok", { label: "OK", tone: "green" }],
  ["unlinked", { label: "non lié", tone: "dim" }],
  ["rank_mismatch", { label: "rang différent", tone: "orange" }],
  ["not_in_rsi_org", { label: "absent de l'org RSI ou caché sur RSI", tone: "red" }],
  ["rsi_unknown", { label: "roster RSI jamais lu", tone: "dim" }],
]);

/** French badge of a reconciliation status (spec § 10.2); null for a bot or an unmapped server. */
export function reconciliationBadge(status: DiscordReconciliation | null): BadgeSpec | null {
  if (status === null) return null;
  return RECONCILIATION_BADGES.get(status) ?? { label: status, tone: "dim" };
}

const EVENT_LABELS = new Map<string, string>([
  ["joined", "arrivée"],
  ["left", "départ"],
  ["rejoined", "retour"],
  ["roles_changed", "rôles"],
  ["nick_changed", "pseudo"],
  ["username_changed", "nom d'utilisateur"],
  ["global_name_changed", "nom affiché"],
]);

/** French name of a Discord event type. */
export function eventTypeLabel(type: string): string {
  return EVENT_LABELS.get(type) ?? type.replace(/_/g, " ");
}

/** Arrivals green, departures red, anything else orange; RSI member changes follow the same colours. */
export function eventTone(type: string): BadgeTone {
  if (type === "joined" || type === "member_joined") return "green";
  if (type === "left" || type === "member_left") return "red";
  return "orange";
}

/** When an event happened: its exact date, else the window between the previous upload and this one. */
export function eventWhen(event: { occurredAt: string | null; notBefore: string | null; observedAt: string }): string {
  if (event.occurredAt) return formatUtc(event.occurredAt);
  if (event.notBefore) return `entre ${formatUtc(event.notBefore)} et ${formatUtc(event.observedAt)}`;
  return `au plus tard ${formatUtc(event.observedAt)}`;
}

/** "Rang : X → Y" for a roles change that changes the member's rank. */
export function rankChangeText(change: { from: string | null; to: string | null }): string {
  return `Rang : ${cleanDiscordText(change.from) ?? "aucun"} → ${cleanDiscordText(change.to) ?? "aucun"}`;
}

export interface RoleRef {
  id: string;
  name: string;
}

/** The [{"id","name"}] JSON of a roles_changed value, names of the time; [] when absent or malformed. */
export function parseRoleList(json: string | null): RoleRef[] {
  if (!json) return [];
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    return [];
  }
  if (!Array.isArray(value)) return [];
  return value.flatMap((item: unknown) => {
    if (typeof item !== "object" || item === null) return [];
    const { id, name } = item as { id?: unknown; name?: unknown };
    return typeof id === "string" && typeof name === "string" ? [{ id, name }] : [];
  });
}

/** Roles gained and lost between the two values of a roles_changed event, compared by id. */
export function rolesDiff(oldJson: string | null, newJson: string | null): { added: RoleRef[]; removed: RoleRef[] } {
  const before = parseRoleList(oldJson);
  const after = parseRoleList(newJson);
  const beforeIds = new Set(before.map((role) => role.id));
  const afterIds = new Set(after.map((role) => role.id));
  return {
    added: after.filter((role) => !beforeIds.has(role.id)),
    removed: before.filter((role) => !afterIds.has(role.id)),
  };
}

const DISCREPANCY_BADGES = new Map<string, BadgeSpec>([
  ["rsi_only", { label: "sur RSI seulement", tone: "orange" }],
  ["not_in_rsi_org", { label: "absent de l'org RSI ou caché sur RSI", tone: "red" }],
  ["rank_mismatch", { label: "rang différent", tone: "orange" }],
]);

/** French badge of a gap between a server and its org's RSI roster. */
export function discrepancyKindBadge(kind: DiscordDiscrepancyKind): BadgeSpec {
  return DISCREPANCY_BADGES.get(kind) ?? { label: kind, tone: "dim" };
}

/** French badge of a suggestion's confidence (spec § 10.1). */
export function confidenceBadge(confidence: DiscordSuggestionConfidence): BadgeSpec {
  return confidence === "strong"
    ? { label: "confiance forte", tone: "green" }
    : { label: "confiance moyenne", tone: "orange" };
}

/** "75 %": a part of a whole, rounded; a dash when the whole is zero. */
export function formatShare(part: number, whole: number): string {
  return whole > 0 ? `${Math.round((part * 100) / whole)} %` : "—";
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/format.test.ts src/lib/discord/params.test.ts`

Expected: `Test Files  2 passed (2)`, `Tests  120 passed (120)` (89 in `format.test.ts`, 31 in `params.test.ts`).

- [ ] **Step 9: Write the server page first (its tabs do not exist yet)**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/page.tsx`:

```tsx
import Link from "next/link";
import { notFound } from "next/navigation";
import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { HudBadge } from "@/components/hud/HudBadge";
import { getDiscordGuild } from "@/lib/api/endpoints";
import { getSession } from "@/lib/auth/session";
import { requireAuthCtx } from "@/lib/auth/server-api";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { parseDiscordTab, type SearchParams } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { snowflakeSchema, valid } from "@/lib/validation";
import { ConfigTab } from "./ConfigTab";
import { GapsTab } from "./GapsTab";
import { GuildAdminActions } from "./GuildAdminActions";
import { GuildTabNav } from "./GuildTabNav";
import { HistoryTab } from "./HistoryTab";
import { loadOrNotFound } from "./load";
import { MembersTab } from "./MembersTab";
import { SuggestionsTab } from "./SuggestionsTab";
import { SyncsTab } from "./SyncsTab";

export const dynamic = "force-dynamic";

interface PageProps {
  params: Promise<{ guildId: string }>;
  searchParams: Promise<SearchParams>;
}

/**
 * A tracked Discord server: header with its org and last upload, admin actions, and one
 * tab at a time (?tab=), each loading only its own data.
 */
export default async function DiscordGuildPage({ params, searchParams }: PageProps) {
  const [{ guildId }, sp] = await Promise.all([params, searchParams]);
  // Not a snowflake: no such server, and nothing forged reaches the API.
  if (!valid(snowflakeSchema, guildId)) notFound();

  const ctx = await requireAuthCtx();
  const [guild, session] = await Promise.all([loadOrNotFound(getDiscordGuild(ctx, guildId)), getSession()]);
  const tab = parseDiscordTab(sp.tab);
  const isAdmin = session?.isAdmin ?? false;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-start justify-between gap-4 border-b border-hud-cyan/30 pb-4">
        <div className="flex min-w-0 flex-1 items-center gap-4">
          <GuildIcon guildId={guild.guildId} iconHash={guild.iconHash} size={56} />
          <div className="min-w-0 flex-1">
            <div className="hud-label">— UEE::DISCORD_SERVER</div>
            <h1 className="mt-1 flex min-w-0 font-display text-3xl">
              <DiscordText value={guild.name} fallback={guild.guildId} />
            </h1>
            <div className="mt-2 flex flex-wrap items-center gap-2 font-mono text-xs text-hud-text-dim">
              {guild.orgSid ? (
                <Link href={`/orgs/${encodeURIComponent(guild.orgSid)}`} className="text-hud-cyan hover:text-hud-orange">
                  [{guild.orgSid}] {guild.orgName ?? ""}
                </Link>
              ) : (
                <HudBadge tone="orange">NON RELIÉ</HudBadge>
              )}
              {guild.orgMappedBy && <span>· relié par {guild.orgMappedBy}</span>}
              <span>· {formatNumber(guild.activeMembers)} actifs</span>
              {guild.lastSync && (
                <>
                  <span>· dernier envoi {formatUtc(guild.lastSync.receivedAt)}</span>
                  {syncBadges(guild.lastSync).map((b) => (
                    <HudBadge key={b.label} tone={b.tone}>
                      {b.label}
                    </HudBadge>
                  ))}
                </>
              )}
            </div>
          </div>
        </div>
        {isAdmin && <GuildAdminActions guildId={guild.guildId} guildName={guild.name} />}
      </header>

      <GuildTabNav guildId={guild.guildId} active={tab} />

      {tab === "members" && <MembersTab ctx={ctx} guild={guild} searchParams={sp} isAdmin={isAdmin} />}
      {tab === "history" && <HistoryTab ctx={ctx} guildId={guild.guildId} searchParams={sp} />}
      {tab === "gaps" && <GapsTab ctx={ctx} guild={guild} />}
      {tab === "suggestions" && <SuggestionsTab ctx={ctx} guild={guild} />}
      {tab === "config" && <ConfigTab guild={guild} />}
      {tab === "syncs" && <SyncsTab ctx={ctx} guildId={guild.guildId} />}
    </div>
  );
}
```

- [ ] **Step 10: Run the typecheck to verify it fails**

Run (from `src/Collector.Web`): `pnpm typecheck`

Expected: `error TS2307: Cannot find module './ConfigTab' or its corresponding type declarations.` in `src/app/(public)/discord/[guildId]/page.tsx`, and the same error for `./GapsTab`, `./GuildAdminActions`, `./GuildTabNav`, `./HistoryTab`, `./load`, `./MembersTab`, `./SuggestionsTab` and `./SyncsTab`.

- [ ] **Step 11: Write the page's frame: data loading, tab bar, unmapped notice, admin actions**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/load.ts`:

```ts
import { notFound } from "next/navigation";
import { ApiError } from "@/lib/api/errors";
import { withAuthRedirect } from "@/lib/auth/server-api";

/**
 * An API read for the server page: a 401 sends to /login, a 404 (the server was
 * deleted meanwhile) to the not-found page; any other error reaches error.tsx.
 */
export async function loadOrNotFound<T>(promise: Promise<T>): Promise<T> {
  try {
    return await withAuthRedirect(promise);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/GuildTabNav.tsx`:

```tsx
import Link from "next/link";
import { DISCORD_TABS, TAB_LABELS, tabHref, type DiscordTab } from "@/lib/discord/params";
import { cn } from "@/lib/utils/cn";

/** Tabs of a server page, as plain links: each tab is its own URL (?tab=). */
export function GuildTabNav({ guildId, active }: { guildId: string; active: DiscordTab }) {
  return (
    <nav aria-label="Onglets du serveur" className="flex flex-wrap gap-1 border-b border-hud-cyan/20 font-mono text-xs">
      {DISCORD_TABS.map((tab) => (
        <Link
          key={tab}
          href={tabHref(guildId, tab)}
          aria-current={tab === active ? "page" : undefined}
          className={cn(
            "relative px-3 py-2 uppercase tracking-[0.2em] transition-colors",
            tab === active ? "text-hud-cyan" : "text-hud-text-dim hover:text-hud-text",
          )}
        >
          {TAB_LABELS[tab]}
          {tab === active && (
            <span className="absolute inset-x-1 -bottom-px h-px bg-hud-cyan shadow-[0_0_8px_var(--hud-cyan)]" />
          )}
        </Link>
      ))}
    </nav>
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/UnmappedNotice.tsx`:

```tsx
import Link from "next/link";
import { tabHref } from "@/lib/discord/params";

/** Shown by the gaps and suggestions tabs while the server is not mapped to an org. */
export function UnmappedNotice({ guildId }: { guildId: string }) {
  return (
    <p className="font-mono text-xs text-hud-orange">
      {"Relie d'abord ce serveur à une corpo "}
      <Link href={tabHref(guildId, "config")} className="underline hover:text-hud-cyan">
        (onglet config)
      </Link>
    </p>
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/GuildAdminActions.tsx`:

```tsx
"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { HudButton } from "@/components/hud/HudButton";
import { cleanDiscordText } from "@/lib/discord/format";
import { allowMassDepartureAction, eraseGuildAction } from "../actions";

/**
 * Admin actions on a server (spec § 9.3 and § 13.2), each behind a confirmation. The
 * page shows them to admins only; the server actions check the role again.
 */
export function GuildAdminActions({ guildId, guildName }: { guildId: string; guildName: string }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const name = cleanDiscordText(guildName) ?? guildId;

  async function allowMassDeparture() {
    if (!confirm(`Autoriser le prochain envoi complet de « ${name} » à enregistrer plus de 25 % de départs ?`)) return;
    setBusy(true);
    const res = await allowMassDepartureAction(guildId);
    setBusy(false);
    if (res.ok) {
      toast.success("Le prochain envoi complet pourra enregistrer un départ massif.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function erase(exclude: boolean) {
    const question = exclude
      ? `Supprimer « ${name} » et l'exclure du suivi ? Toutes ses données Discord sont effacées et ses envois suivants seront refusés.`
      : `Supprimer « ${name} » et repartir d'une base ? Toutes ses données Discord sont effacées ; son prochain envoi sera une nouvelle base.`;
    if (!confirm(question)) return;
    setBusy(true);
    const res = await eraseGuildAction(guildId, exclude);
    setBusy(false);
    if (res.ok) {
      toast.success(exclude ? "Serveur supprimé et exclu." : "Serveur supprimé : son prochain envoi repartira d'une base.");
      router.push("/discord");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <div className="flex flex-col items-end gap-2">
      <span className="hud-label text-hud-red">ADMINISTRATION</span>
      <div className="flex flex-wrap justify-end gap-2">
        <HudButton type="button" variant="orange" disabled={busy} onClick={allowMassDeparture}>
          Autoriser un départ massif
        </HudButton>
        <HudButton type="button" variant="danger" disabled={busy} onClick={() => erase(false)}>
          {"Supprimer et repartir d'une base"}
        </HudButton>
        <HudButton type="button" variant="danger" disabled={busy} onClick={() => erase(true)}>
          Supprimer et exclure ce serveur
        </HudButton>
      </div>
    </div>
  );
}
```

- [ ] **Step 12: Write the shared history components**

Create `src/Collector.Web/src/components/discord/RoleChanges.tsx`:

```tsx
import { rolesDiff } from "@/lib/discord/format";
import { DiscordText } from "./DiscordText";

/** Roles gained (+) and lost (−) by a roles_changed event, with the names they had then. */
export function RoleChanges({ oldValue, newValue }: { oldValue: string | null; newValue: string | null }) {
  const { added, removed } = rolesDiff(oldValue, newValue);
  if (added.length === 0 && removed.length === 0) return null;
  return (
    <span className="flex min-w-0 flex-wrap items-center gap-2">
      {added.map((role) => (
        <span key={`+${role.id}`} className="inline-flex min-w-0 max-w-[12rem] items-center text-hud-green">
          +<DiscordText value={role.name} fallback={role.id} />
        </span>
      ))}
      {removed.map((role) => (
        <span key={`-${role.id}`} className="inline-flex min-w-0 max-w-[12rem] items-center text-hud-red">
          −<DiscordText value={role.name} fallback={role.id} />
        </span>
      ))}
    </span>
  );
}
```

Create `src/Collector.Web/src/components/discord/ValueChange.tsx`:

```tsx
import { DiscordText } from "./DiscordText";

/** Old → new value of a nick, name or handle change. */
export function ValueChange({ oldValue, newValue }: { oldValue: string | null; newValue: string | null }) {
  return (
    <span className="inline-flex min-w-0 items-center gap-2 text-hud-text-dim">
      <span className="inline-flex min-w-0 max-w-[12rem] text-hud-red/80 line-through">
        <DiscordText value={oldValue} />
      </span>
      <span aria-hidden>→</span>
      <span className="inline-flex min-w-0 max-w-[12rem] text-hud-green">
        <DiscordText value={newValue} />
      </span>
    </span>
  );
}
```

Create `src/Collector.Web/src/components/discord/LinkedPeople.tsx`:

```tsx
import Link from "next/link";
import { HudBadge } from "@/components/hud/HudBadge";
import type { DiscordLinkedPersonDto } from "@/lib/api/types";

/** The citizens a Discord account is linked to, each a link to its page. */
export function LinkedPeople({ links, multiple = false }: { links: DiscordLinkedPersonDto[]; multiple?: boolean }) {
  if (links.length === 0) return <span className="text-hud-text-dim">—</span>;
  return (
    <span className="flex min-w-0 flex-col items-start gap-0.5">
      {links.map((person, i) =>
        person.handle ? (
          <Link
            key={`${person.handle}-${i}`}
            href={`/users/${encodeURIComponent(person.handle)}`}
            title={person.displayName ?? person.handle}
            className="block max-w-full truncate text-hud-cyan hover:text-hud-orange"
          >
            {person.handle}
          </Link>
        ) : (
          <span key={`citizen-${person.citizenId ?? i}`} className="text-hud-text-dim">
            #{person.citizenId ?? "?"}
          </span>
        ),
      )}
      {multiple && <HudBadge tone="orange">PLUSIEURS LIENS</HudBadge>}
    </span>
  );
}
```

- [ ] **Step 13: Write the members tab**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/MemberFilterForm.tsx`:

```tsx
import Link from "next/link";
import type { DiscordRoleDto } from "@/lib/api/types";
import { cleanDiscordText, reconciliationBadge } from "@/lib/discord/format";
import { MAX_SEARCH_LENGTH, RECONCILIATIONS, tabHref, type MemberFilters } from "@/lib/discord/params";

export const FILTER_FIELD_CLASS =
  "hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-2 py-1.5 font-mono text-xs text-hud-text focus:border-hud-cyan focus:outline-none";

export const FILTER_BUTTON_CLASS =
  "hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10";

interface MemberFilterFormProps {
  guildId: string;
  filters: MemberFilters;
  /** Rank roles offered in the rank filter (not deleted). */
  rankRoles: DiscordRoleDto[];
  /** The reconciliation filter only means something for a mapped server. */
  mapped: boolean;
}

/** GET form of the members tab: filters live in the URL, so a filtered page can be shared. */
export function MemberFilterForm({ guildId, filters, rankRoles, mapped }: MemberFilterFormProps) {
  return (
    <form action={`/discord/${encodeURIComponent(guildId)}`} method="get" className="mb-4 flex flex-wrap items-end gap-2">
      <label className="flex flex-col gap-1">
        <span className="hud-label">STATUT</span>
        <select name="status" defaultValue={filters.status} className={FILTER_FIELD_CLASS}>
          <option value="active">présents</option>
          <option value="former">partis</option>
          <option value="all">tous</option>
        </select>
      </label>
      <label className="flex flex-col gap-1">
        <span className="hud-label">RECHERCHE</span>
        <input
          name="search"
          type="search"
          defaultValue={filters.search ?? ""}
          maxLength={MAX_SEARCH_LENGTH}
          placeholder="nom, pseudo…"
          className={FILTER_FIELD_CLASS}
        />
      </label>
      <label className="flex flex-col gap-1">
        <span className="hud-label">RANG</span>
        <select name="rankRoleId" defaultValue={filters.rankRoleId ?? ""} className={`${FILTER_FIELD_CLASS} max-w-[14rem]`}>
          <option value="">tous les rangs</option>
          {rankRoles.map((role) => (
            <option key={role.roleId} value={role.roleId}>
              {cleanDiscordText(role.name) ?? role.roleId}
            </option>
          ))}
        </select>
      </label>
      {mapped && (
        <label className="flex flex-col gap-1">
          <span className="hud-label">RECOUPEMENT</span>
          <select name="reconciliation" defaultValue={filters.reconciliation ?? ""} className={FILTER_FIELD_CLASS}>
            <option value="">tous</option>
            {RECONCILIATIONS.map((status) => (
              <option key={status} value={status}>
                {reconciliationBadge(status)?.label ?? status}
              </option>
            ))}
          </select>
        </label>
      )}
      <button type="submit" className={FILTER_BUTTON_CLASS}>
        FILTRER
      </button>
      <Link
        href={tabHref(guildId, "members")}
        className="px-2 py-1.5 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-cyan"
      >
        effacer
      </Link>
    </form>
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/DiscordMembersTable.tsx`:

```tsx
"use client";
import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { DiscordText } from "@/components/discord/DiscordText";
import { LinkedPeople } from "@/components/discord/LinkedPeople";
import { RoleDot } from "@/components/discord/RoleDot";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordMemberDto } from "@/lib/api/types";
import { discordDisplayName, formatUtc, reconciliationBadge } from "@/lib/discord/format";
import { tabHref } from "@/lib/discord/params";
import { eraseAccountAction } from "../actions";

interface DiscordMembersTableProps {
  rows: DiscordMemberDto[];
  guildId: string;
  /** Adds « Supprimer et exclure ce compte » on each row; the action checks the role again. */
  isAdmin: boolean;
}

/** One API page of members: name, nick, rank, linked citizens, RSI rank and reconciliation status. */
export function DiscordMembersTable({ rows, guildId, isAdmin }: DiscordMembersTableProps) {
  const router = useRouter();
  const [erasing, setErasing] = useState<string | null>(null);

  async function erase(member: DiscordMemberDto) {
    const question =
      `Supprimer et exclure le compte « ${discordDisplayName(member)} » (${member.discordUserId}) ? ` +
      "Ses données Discord sont effacées de tous les serveurs et les envois suivants l'ignoreront.";
    if (!confirm(question)) return;
    setErasing(member.discordUserId);
    const res = await eraseAccountAction(member.discordUserId);
    setErasing(null);
    if (res.ok) {
      toast.success("Compte effacé et exclu.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  const columns: HudColumn<DiscordMemberDto>[] = [
    {
      key: "member",
      header: "MEMBRE",
      width: "min-w-0 flex-1",
      render: (m) => (
        <span className="flex min-w-0 flex-col">
          <span className="flex min-w-0 items-center gap-2">
            <Link
              href={tabHref(guildId, "history", { userId: m.discordUserId })}
              title="Historique de ce membre"
              className="inline-flex min-w-0 max-w-full text-hud-text hover:text-hud-cyan"
            >
              <DiscordText value={discordDisplayName(m)} />
            </Link>
            {m.isBot && <HudBadge tone="dim">BOT</HudBadge>}
          </span>
          <span className="inline-flex min-w-0 max-w-full text-[10px] text-hud-text-dim">
            @<DiscordText value={m.username} />
          </span>
        </span>
      ),
    },
    {
      key: "nick",
      header: "PSEUDO",
      width: "w-36 min-w-0",
      render: (m) => <DiscordText value={m.nick} />,
    },
    {
      key: "rank",
      header: "RANG",
      width: "w-40 min-w-0",
      render: (m) =>
        m.rank ? (
          <span className="inline-flex min-w-0 max-w-full items-center gap-1 border border-hud-cyan/30 px-1.5 py-0.5">
            <RoleDot color={m.rank.color} />
            <DiscordText value={m.rank.name} />
          </span>
        ) : (
          <span className="text-hud-text-dim">—</span>
        ),
    },
    {
      key: "links",
      header: "CITOYENS LIÉS",
      width: "w-40 min-w-0",
      render: (m) => <LinkedPeople links={m.links} multiple={m.multipleLinks} />,
    },
    {
      key: "rsiRank",
      header: "RANG RSI",
      width: "w-32 min-w-0",
      render: (m) => <DiscordText value={m.rsiRank} />,
    },
    {
      key: "reconciliation",
      header: "RECOUPEMENT",
      width: "w-44",
      render: (m) => {
        const badge = reconciliationBadge(m.reconciliation);
        return badge ? <HudBadge tone={badge.tone}>{badge.label}</HudBadge> : <span className="text-hud-text-dim">—</span>;
      },
    },
    {
      key: "dates",
      header: "ARRIVÉE",
      width: "w-36",
      render: (m) => (
        <span className="flex flex-col text-[10px] text-hud-text-dim">
          <span>{formatUtc(m.joinedAt)}</span>
          {m.leftAt && <span className="text-hud-red">parti {formatUtc(m.leftAt)}</span>}
        </span>
      ),
    },
  ];

  if (isAdmin) {
    columns.push({
      key: "erase",
      header: "",
      width: "w-32",
      align: "right",
      render: (m) => (
        <button
          type="button"
          disabled={erasing !== null}
          onClick={() => erase(m)}
          className="text-right font-mono text-[10px] uppercase tracking-wide text-hud-red/80 hover:text-hud-red disabled:opacity-40"
        >
          {erasing === m.discordUserId ? "…" : "Supprimer et exclure ce compte"}
        </button>
      ),
    });
  }

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(m) => m.discordUserId}
      empty="Aucun membre ne correspond à ces filtres."
    />
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/MembersTab.tsx`:

```tsx
import { HudPanel } from "@/components/hud/HudPanel";
import { Pagination } from "@/components/layout/Pagination";
import { getDiscordMembers } from "@/lib/api/endpoints";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { MEMBERS_PAGE_SIZE, parseMemberFilters, type SearchParams } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { DiscordMembersTable } from "./DiscordMembersTable";
import { loadOrNotFound } from "./load";
import { MemberFilterForm } from "./MemberFilterForm";

interface MembersTabProps {
  ctx: AuthCtx;
  guild: DiscordGuildDetailDto;
  searchParams: SearchParams;
  isAdmin: boolean;
}

/** Members paged by the API, filtered through the URL (status, search, rank, reconciliation). */
export async function MembersTab({ ctx, guild, searchParams, isAdmin }: MembersTabProps) {
  const filters = parseMemberFilters(searchParams);
  const mapped = guild.orgSid !== null;
  const page = await loadOrNotFound(
    getDiscordMembers(ctx, guild.guildId, {
      status: filters.status,
      search: filters.search,
      rankRoleId: filters.rankRoleId,
      // The API ignores it for an unmapped server: not sent either.
      reconciliation: mapped ? filters.reconciliation : undefined,
      page: filters.page,
      pageSize: MEMBERS_PAGE_SIZE,
    }),
  );
  const rankRoles = guild.roles.filter((role) => role.isRank && !role.deleted);

  return (
    <HudPanel label={`MEMBRES · ${formatNumber(page.total)}`}>
      <MemberFilterForm guildId={guild.guildId} filters={filters} rankRoles={rankRoles} mapped={mapped} />
      <DiscordMembersTable rows={page.items} guildId={guild.guildId} isAdmin={isAdmin} />
      {page.totalPages > 1 && (
        <Pagination param="page" page={page.page} totalPages={page.totalPages} total={page.total} />
      )}
    </HudPanel>
  );
}
```

- [ ] **Step 14: Write the history tab**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/HistoryTab.tsx`:

```tsx
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { RoleChanges } from "@/components/discord/RoleChanges";
import { ValueChange } from "@/components/discord/ValueChange";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudPanel } from "@/components/hud/HudPanel";
import { getDiscordEvents } from "@/lib/api/endpoints";
import type { DiscordEventDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { eventTone, eventTypeLabel, eventWhen, rankChangeText } from "@/lib/discord/format";
import { EVENT_TYPES, HISTORY_LIMIT, parseEventFilters, tabHref, type SearchParams } from "@/lib/discord/params";
import { loadOrNotFound } from "./load";
import { FILTER_BUTTON_CLASS, FILTER_FIELD_CLASS } from "./MemberFilterForm";

const NAME_CHANGES = new Set(["nick_changed", "username_changed", "global_name_changed"]);

/** What changed: « Rang : X → Y », the roles gained and lost, or the old and new name. */
function EventDetail({ event }: { event: DiscordEventDto }) {
  return (
    <>
      {event.rankChange && (
        <span className="inline-flex min-w-0 max-w-[24rem] text-hud-orange">
          <DiscordText value={rankChangeText(event.rankChange)} />
        </span>
      )}
      {event.type === "roles_changed" && <RoleChanges oldValue={event.oldValue} newValue={event.newValue} />}
      {NAME_CHANGES.has(event.type) && <ValueChange oldValue={event.oldValue} newValue={event.newValue} />}
    </>
  );
}

interface HistoryTabProps {
  ctx: AuthCtx;
  guildId: string;
  searchParams: SearchParams;
}

/** The server's history, newest first, with who sent the upload that recorded each event. */
export async function HistoryTab({ ctx, guildId, searchParams }: HistoryTabProps) {
  const filters = parseEventFilters(searchParams);
  const events = await loadOrNotFound(
    getDiscordEvents(ctx, guildId, { type: filters.type, userId: filters.userId, limit: HISTORY_LIMIT }),
  );
  const filtered = filters.type !== undefined || filters.userId !== undefined;

  return (
    <HudPanel label={`HISTORIQUE · ${events.length} ÉVÉNEMENTS`} accent="orange">
      <form action={`/discord/${encodeURIComponent(guildId)}`} method="get" className="mb-4 flex flex-wrap items-end gap-2">
        <input type="hidden" name="tab" value="history" />
        {filters.userId && <input type="hidden" name="userId" value={filters.userId} />}
        <label className="flex flex-col gap-1">
          <span className="hud-label">TYPE</span>
          <select name="type" defaultValue={filters.type ?? ""} className={FILTER_FIELD_CLASS}>
            <option value="">tous</option>
            {EVENT_TYPES.map((type) => (
              <option key={type} value={type}>
                {eventTypeLabel(type)}
              </option>
            ))}
          </select>
        </label>
        <button type="submit" className={FILTER_BUTTON_CLASS}>
          FILTRER
        </button>
        {filtered && (
          <Link
            href={tabHref(guildId, "history")}
            className="px-2 py-1.5 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-cyan"
          >
            {"tout l'historique"}
          </Link>
        )}
      </form>
      <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
        {filters.userId ? `Compte ${filters.userId} seulement · ` : ""}
        {`${HISTORY_LIMIT} derniers événements au plus.`}
      </p>
      {events.length === 0 ? (
        <div className="py-6 text-center font-mono text-xs text-hud-text-dim">— aucun événement —</div>
      ) : (
        <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
          {events.map((event) => (
            <li key={event.id} className="flex flex-col gap-1 py-2">
              <div className="flex min-w-0 flex-wrap items-center gap-2">
                <HudBadge tone={eventTone(event.type)}>{eventTypeLabel(event.type)}</HudBadge>
                <Link
                  href={tabHref(guildId, "history", { userId: event.discordUserId })}
                  className="inline-flex min-w-0 max-w-[16rem] text-hud-cyan hover:text-hud-orange"
                >
                  <DiscordText value={event.username} fallback={event.discordUserId} />
                </Link>
                <EventDetail event={event} />
              </div>
              <div className="text-[10px] text-hud-text-dim">
                {eventWhen(event)}
                {event.submittedBy ? ` · envoi de ${event.submittedBy}` : ""}
              </div>
            </li>
          ))}
        </ul>
      )}
    </HudPanel>
  );
}
```

- [ ] **Step 15: Write the RSI gaps tab**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/DiscrepanciesTable.tsx`:

```tsx
"use client";
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordDiscrepancyDto } from "@/lib/api/types";
import { discrepancyKindBadge } from "@/lib/discord/format";

/** Gaps between the server and its org's RSI roster; paged in the browser (rsi_only can be long). */
export function DiscrepanciesTable({ rows }: { rows: DiscordDiscrepancyDto[] }) {
  const columns: HudColumn<DiscordDiscrepancyDto>[] = [
    {
      key: "kind",
      header: "ÉCART",
      width: "w-60",
      sortable: true,
      sortValue: (d) => d.kind,
      render: (d) => {
        const badge = discrepancyKindBadge(d.kind);
        return <HudBadge tone={badge.tone}>{badge.label}</HudBadge>;
      },
    },
    {
      key: "citizen",
      header: "CITOYEN RSI",
      width: "w-44 min-w-0",
      sortable: true,
      sortValue: (d) => d.handle?.toLowerCase() ?? null,
      render: (d) =>
        d.handle ? (
          <Link
            href={`/users/${encodeURIComponent(d.handle)}`}
            className="block truncate text-hud-cyan hover:text-hud-orange"
          >
            {d.handle}
          </Link>
        ) : (
          <span className="text-hud-text-dim">{d.citizenId !== null ? `#${d.citizenId}` : "—"}</span>
        ),
    },
    {
      key: "discord",
      header: "COMPTE DISCORD",
      width: "min-w-0 flex-1",
      render: (d) => <DiscordText value={d.discordName} fallback={d.discordUserId ?? "—"} />,
    },
    {
      key: "discordRank",
      header: "RANG DISCORD",
      width: "w-40 min-w-0",
      render: (d) => <DiscordText value={d.discordRank} />,
    },
    {
      key: "rsiRank",
      header: "RANG RSI",
      width: "w-40 min-w-0",
      render: (d) => <DiscordText value={d.rsiRank} />,
    },
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(d, i) => `${d.kind}:${d.discordUserId ?? ""}:${d.handle ?? ""}:${i}`}
      empty="Aucun écart."
      paginated
      pageSizeOptions={[25, 50, 100, 0]}
    />
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/GapsTab.tsx`:

```tsx
import { HudPanel } from "@/components/hud/HudPanel";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { getDiscordDiscrepancies } from "@/lib/api/endpoints";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { formatShare, formatUtc } from "@/lib/discord/format";
import { DiscrepanciesTable } from "./DiscrepanciesTable";
import { loadOrNotFound } from "./load";
import { UnmappedNotice } from "./UnmappedNotice";

/** Totals on both sides and the gaps with the mapped org's RSI roster (spec § 10.2). */
export async function GapsTab({ ctx, guild }: { ctx: AuthCtx; guild: DiscordGuildDetailDto }) {
  const gaps = await loadOrNotFound(getDiscordDiscrepancies(ctx, guild.guildId));
  if (gaps.orgSid === null || gaps.totals === null) {
    return (
      <HudPanel label="ÉCARTS RSI" accent="orange">
        <UnmappedNotice guildId={guild.guildId} />
      </HudPanel>
    );
  }

  const totals = gaps.totals;
  const readAt = `lu le ${formatUtc(totals.rsiCountsAt)}`;
  return (
    <div className="flex flex-col gap-6">
      <section className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <HudStatTile label="Discord actifs" value={totals.discordActive} sub="hors bots" accent="cyan" />
        <HudStatTile
          label="Liés à RSI"
          value={totals.discordLinked}
          sub={`${formatShare(totals.discordLinked, totals.discordActive)} des actifs`}
          accent="green"
        />
        {totals.rsiBreakdownKnown ? (
          <HudStatTile
            label="RSI visibles"
            value={totals.rsiVisible ?? 0}
            sub={`masqués ${totals.rsiRedacted ?? 0} · cachés ${totals.rsiHidden ?? 0} · ${readAt}`}
            accent="orange"
          />
        ) : (
          <HudStatTile
            label="RSI (total)"
            value={totals.rsiTotalRows ?? "—"}
            sub={`répartition inconnue · ${readAt}`}
            accent="orange"
          />
        )}
        <HudStatTile
          label="Écarts"
          value={gaps.items.length}
          sub={gaps.rsiOnlyAvailable ? "absences comprises" : "absences inconnues"}
          accent="red"
        />
      </section>

      <HudPanel label={`ÉCARTS AVEC ${gaps.orgSid} · ${gaps.items.length}`} accent="orange">
        {!gaps.rsiOnlyAvailable && (
          <p className="mb-3 font-mono text-xs text-hud-orange">Aucun envoi complet : absences inconnues</p>
        )}
        <DiscrepanciesTable rows={gaps.items} />
      </HudPanel>
    </div>
  );
}
```

- [ ] **Step 16: Write the suggestions tab**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/SuggestionsList.tsx`:

```tsx
"use client";
import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { DiscordText } from "@/components/discord/DiscordText";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudButton } from "@/components/hud/HudButton";
import type { DiscordSuggestionDto } from "@/lib/api/types";
import { cleanDiscordText, confidenceBadge } from "@/lib/discord/format";
import { acceptSuggestionAction, rejectSuggestionAction, undoRejectionAction } from "../actions";

const keyOf = (s: DiscordSuggestionDto) => `${s.discordUserId}:${s.citizenId ?? `h:${s.handle.toLowerCase()}`}`;

/**
 * Link suggestions with « Valider » (creates the link) and « Ignorer » (stops proposing
 * it; the toast offers to undo). The list is read again with router.refresh().
 */
export function SuggestionsList({ suggestions }: { suggestions: DiscordSuggestionDto[] }) {
  const router = useRouter();
  const [busyKey, setBusyKey] = useState<string | null>(null);

  async function accept(s: DiscordSuggestionDto) {
    setBusyKey(keyOf(s));
    const res = await acceptSuggestionAction(s.discordUserId, s.citizenId, s.handle);
    setBusyKey(null);
    if (res.ok) {
      toast.success(`Lien validé : ${cleanDiscordText(s.discordName) ?? s.discordUserId} → ${res.data?.handle ?? s.handle}.`);
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function undo(id: number) {
    const res = await undoRejectionAction(id);
    if (res.ok) {
      toast.success("Suggestion rétablie.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function reject(s: DiscordSuggestionDto) {
    setBusyKey(keyOf(s));
    const res = await rejectSuggestionAction(s.discordUserId, s.citizenId, s.handle);
    setBusyKey(null);
    if (!res.ok) {
      toast.error(res.error ?? "Échec.");
      return;
    }
    router.refresh();
    const rejectionId = res.data?.id;
    toast.success(
      `Suggestion ignorée : ${s.handle}.`,
      rejectionId === undefined
        ? undefined
        : { action: { label: "Annuler", onClick: () => void undo(rejectionId) } },
    );
  }

  if (suggestions.length === 0) {
    return (
      <p className="py-6 text-center font-mono text-xs text-hud-text-dim">
        Aucune suggestion : chaque membre est lié, ou aucun nom ne correspond à un handle RSI.
      </p>
    );
  }

  return (
    <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
      {suggestions.map((s) => {
        const key = keyOf(s);
        const badge = confidenceBadge(s.confidence);
        return (
          <li key={key} className="flex flex-wrap items-center gap-3 py-2">
            <span className="flex min-w-0 flex-1 flex-wrap items-center gap-2">
              <span className="inline-flex min-w-0 max-w-[16rem] text-hud-text">
                <DiscordText value={s.discordName} fallback={s.discordUserId} />
              </span>
              <span aria-hidden className="text-hud-text-dim">
                →
              </span>
              <Link href={`/users/${encodeURIComponent(s.handle)}`} className="text-hud-cyan hover:text-hud-orange">
                {s.handle}
              </Link>
              {s.displayName && <span className="max-w-[12rem] truncate text-hud-text-dim">{s.displayName}</span>}
              {s.citizenId !== null && <HudBadge tone="dim">#{s.citizenId}</HudBadge>}
              <HudBadge tone={badge.tone}>{badge.label}</HudBadge>
              <span className="inline-flex min-w-0 max-w-[14rem] items-center gap-1 text-[10px] text-hud-text-dim">
                jeton <DiscordText value={s.matchedToken} />
              </span>
            </span>
            <span className="flex gap-2">
              <HudButton type="button" className="px-2 py-1" disabled={busyKey !== null} onClick={() => accept(s)}>
                {busyKey === key ? "…" : "Valider"}
              </HudButton>
              <HudButton
                type="button"
                variant="ghost"
                className="px-2 py-1"
                disabled={busyKey !== null}
                onClick={() => reject(s)}
              >
                Ignorer
              </HudButton>
            </span>
          </li>
        );
      })}
    </ul>
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/SuggestionsTab.tsx`:

```tsx
import { HudPanel } from "@/components/hud/HudPanel";
import { getDiscordSuggestions } from "@/lib/api/endpoints";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { loadOrNotFound } from "./load";
import { SuggestionsList } from "./SuggestionsList";
import { UnmappedNotice } from "./UnmappedNotice";

/** Proposed Discord ↔ RSI links for the unlinked members; only validated links count. */
export async function SuggestionsTab({ ctx, guild }: { ctx: AuthCtx; guild: DiscordGuildDetailDto }) {
  const suggestions = await loadOrNotFound(getDiscordSuggestions(ctx, guild.guildId));

  return (
    <HudPanel label={`SUGGESTIONS DE LIENS · ${suggestions.length}`}>
      {guild.orgSid === null && (
        <div className="mb-3">
          <UnmappedNotice guildId={guild.guildId} />
        </div>
      )}
      <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
        {guild.orgSid === null
          ? "Sans corpo reliée, seules les correspondances de confiance moyenne sont proposées."
          : "Confiance forte : le handle est membre actif de la corpo reliée. Seuls les liens validés comptent."}
      </p>
      <SuggestionsList suggestions={suggestions} />
    </HudPanel>
  );
}
```

- [ ] **Step 17: Write the config tab**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/RoleConfigTable.tsx`:

```tsx
"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { DiscordText } from "@/components/discord/DiscordText";
import { RoleDot } from "@/components/discord/RoleDot";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudButton } from "@/components/hud/HudButton";
import type { DiscordRoleDto } from "@/lib/api/types";
import { cleanDiscordText } from "@/lib/discord/format";
import { parseRankOrderInput } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { updateGuildRoleAction } from "../actions";

const fieldClass =
  "hud-clip w-full border border-hud-cyan-dim bg-hud-bg/60 px-2 py-1 font-mono text-xs text-hud-text focus:border-hud-cyan focus:outline-none disabled:opacity-40";

interface RoleConfigRowProps {
  guildId: string;
  role: DiscordRoleDto;
  rsiRanks: string[];
  canEdit: boolean;
}

function RoleConfigRow({ guildId, role, rsiRanks, canEdit }: RoleConfigRowProps) {
  const router = useRouter();
  const savedOrder = role.rankOrder === null ? "" : String(role.rankOrder);
  const savedLabel = role.rsiRankLabel ?? "";
  const [isRank, setIsRank] = useState(role.isRank);
  const [order, setOrder] = useState(savedOrder);
  const [label, setLabel] = useState(savedLabel);
  const [busy, setBusy] = useState(false);
  const name = cleanDiscordText(role.name) ?? role.roleId;
  const dirty = isRank !== role.isRank || order !== savedOrder || label !== savedLabel;
  // A label chosen earlier may no longer be among the org's RSI ranks: keep it selectable.
  const labels = label !== "" && !rsiRanks.includes(label) ? [label, ...rsiRanks] : rsiRanks;
  const disabled = !canEdit || busy;

  async function save() {
    const parsedOrder = parseRankOrderInput(order);
    if (!parsedOrder.ok) {
      toast.error("Ordre : un nombre entier de 0 à 1000, ou vide.");
      return;
    }
    setBusy(true);
    const res = await updateGuildRoleAction(guildId, role.roleId, isRank, parsedOrder.value, label === "" ? null : label);
    setBusy(false);
    if (res.ok) {
      toast.success(`Rôle « ${name} » enregistré.`);
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <tr className="border-b border-hud-cyan/10">
      <td className="px-2 py-2">
        <span className="flex min-w-0 items-center gap-2">
          <RoleDot color={role.color} />
          <DiscordText value={role.name} fallback={role.roleId} className="min-w-0" />
          {role.managed && <HudBadge tone="dim">GÉRÉ</HudBadge>}
          {role.deleted && <HudBadge tone="red">SUPPRIMÉ</HudBadge>}
        </span>
      </td>
      <td className="px-2 py-2 text-right tabular-nums text-hud-text-dim">{formatNumber(role.memberCount)}</td>
      <td className="px-2 py-2 text-center">
        <input
          type="checkbox"
          checked={isRank}
          onChange={(e) => setIsRank(e.target.checked)}
          disabled={disabled}
          aria-label={`${name} est un rang`}
          className="accent-hud-cyan"
        />
      </td>
      <td className="px-2 py-2">
        <input
          type="number"
          min={0}
          max={1000}
          step={1}
          value={order}
          onChange={(e) => setOrder(e.target.value)}
          disabled={disabled}
          placeholder={String(role.position)}
          aria-label={`Ordre du rang ${name}`}
          className={fieldClass}
        />
      </td>
      <td className="px-2 py-2">
        <select
          value={label}
          onChange={(e) => setLabel(e.target.value)}
          disabled={disabled}
          aria-label={`Rang RSI équivalent à ${name}`}
          className={fieldClass}
        >
          <option value="">— aucun —</option>
          {labels.map((rank) => (
            <option key={rank} value={rank}>
              {rank}
            </option>
          ))}
        </select>
      </td>
      <td className="px-2 py-2 text-right">
        <HudButton type="button" className="px-2 py-1" disabled={disabled || !dirty} onClick={save}>
          {busy ? "…" : "Enregistrer"}
        </HudButton>
      </td>
    </tr>
  );
}

interface RoleConfigTableProps {
  guildId: string;
  roles: DiscordRoleDto[];
  /** RSI ranks of the mapped org, offered as equivalents. */
  rsiRanks: string[];
  canEdit: boolean;
}

/**
 * The server's roles: rank or not, rank order and RSI equivalent. A blank order is sent
 * as null: the API keeps a rank's own order and gives a role that becomes a rank its
 * Discord position (C6). Read-only unless the caller may configure the server.
 */
export function RoleConfigTable({ guildId, roles, rsiRanks, canEdit }: RoleConfigTableProps) {
  const ordered = [...roles].sort((a, b) => Number(a.deleted) - Number(b.deleted) || b.position - a.position);
  if (ordered.length === 0) {
    return <p className="py-4 text-center font-mono text-xs text-hud-text-dim">Aucun rôle reçu.</p>;
  }
  return (
    <div className="w-full overflow-x-auto">
      <table className="w-full min-w-[40rem] table-fixed font-mono text-xs">
        <thead>
          <tr className="border-b border-hud-cyan/30 text-left text-[10px] uppercase tracking-[0.15em] text-hud-text-dim">
            <th className="px-2 py-2 font-normal">RÔLE</th>
            <th className="w-20 px-2 py-2 text-right font-normal">MEMBRES</th>
            <th className="w-16 px-2 py-2 text-center font-normal">RANG</th>
            <th className="w-24 px-2 py-2 font-normal">ORDRE</th>
            <th className="w-48 px-2 py-2 font-normal">RANG RSI</th>
            <th className="w-32 px-2 py-2" />
          </tr>
        </thead>
        <tbody>
          {ordered.map((role) => (
            <RoleConfigRow
              // Saved values in the key: after router.refresh() the row starts from what the API stored.
              key={`${role.roleId}:${role.isRank}:${role.rankOrder}:${role.rsiRankLabel}`}
              guildId={guildId}
              role={role}
              rsiRanks={rsiRanks}
              canEdit={canEdit}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/ConfigTab.tsx`:

```tsx
import { HudPanel } from "@/components/hud/HudPanel";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import { GuildOrgForm } from "../GuildOrgForm";
import { RoleConfigTable } from "./RoleConfigTable";

/**
 * The org the server stands for and its rank roles. Anyone signed in may configure an
 * unmapped server; once mapped, only the responsible user and admins (guild.canEdit).
 */
export function ConfigTab({ guild }: { guild: DiscordGuildDetailDto }) {
  const lockedMessage = `Seuls ${guild.orgMappedBy ?? "le responsable"} et les administrateurs peuvent modifier ce serveur.`;
  return (
    <div className="flex flex-col gap-6">
      <HudPanel label="CORPO RELIÉE">
        <p className="mb-3 font-mono text-xs text-hud-text-dim">
          {guild.orgSid
            ? `Relié à ${guild.orgSid}${guild.orgMappedBy ? ` par ${guild.orgMappedBy}` : ""}.`
            : "Ce serveur n'est relié à aucune corpo : tout utilisateur connecté peut le relier."}
        </p>
        <GuildOrgForm guildId={guild.guildId} currentSid={guild.orgSid} canEdit={guild.canEdit} />
        {!guild.canEdit && <p className="mt-3 font-mono text-xs text-hud-orange">{lockedMessage}</p>}
      </HudPanel>

      <HudPanel label={`RÔLES · ${guild.roles.length}`}>
        <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
          {"Les rôles cochés sont des rangs. Le rang d'un membre est son rôle-rang d'ordre le plus élevé ; le rang RSI équivalent sert aux recoupements."}
        </p>
        <RoleConfigTable guildId={guild.guildId} roles={guild.roles} rsiRanks={guild.rsiRanks} canEdit={guild.canEdit} />
      </HudPanel>
    </div>
  );
}
```

- [ ] **Step 18: Write the uploads tab**

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/SyncsTable.tsx`:

```tsx
"use client";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordSyncDto } from "@/lib/api/types";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { formatNumber } from "@/lib/utils/format";

/** The upload journal: who, when, how, how many, and the complete / partial / guard / baseline badges. */
export function SyncsTable({ rows }: { rows: DiscordSyncDto[] }) {
  const columns: HudColumn<DiscordSyncDto>[] = [
    {
      key: "received",
      header: "REÇU",
      width: "w-44",
      render: (s) => (
        <span className="flex flex-col">
          <span>{formatUtc(s.receivedAt)}</span>
          <span className="text-[10px] text-hud-text-dim">collecte {formatUtc(s.collectedAt)}</span>
        </span>
      ),
    },
    {
      key: "by",
      header: "PAR",
      width: "w-32 min-w-0",
      render: (s) => <span className="block truncate">{s.submittedBy}</span>,
    },
    { key: "method", header: "MÉTHODE", width: "w-32", render: (s) => s.method },
    {
      key: "members",
      header: "MEMBRES",
      width: "w-32",
      align: "right",
      render: (s) =>
        s.expectedCount === null
          ? formatNumber(s.collectedCount)
          : `${formatNumber(s.collectedCount)} / ${formatNumber(s.expectedCount)}`,
    },
    { key: "optedOut", header: "EXCLUS", width: "w-20", align: "right", render: (s) => formatNumber(s.optedOutCount) },
    { key: "events", header: "ÉVÉNEMENTS", width: "w-24", align: "right", render: (s) => formatNumber(s.eventCount) },
    {
      key: "state",
      header: "ÉTAT",
      width: "min-w-0 flex-1",
      render: (s) => (
        <span className="flex flex-wrap gap-1">
          {syncBadges(s).map((b) => (
            <HudBadge key={b.label} tone={b.tone}>
              {b.label}
            </HudBadge>
          ))}
        </span>
      ),
    },
    { key: "version", header: "PLUGIN", width: "w-20", render: (s) => s.pluginVersion },
  ];

  return <HudDataGrid columns={columns} rows={rows} rowKey={(s) => String(s.id)} empty="Aucun envoi." />;
}
```

Create `src/Collector.Web/src/app/(public)/discord/[guildId]/SyncsTab.tsx`:

```tsx
import { HudPanel } from "@/components/hud/HudPanel";
import { getDiscordSyncs } from "@/lib/api/endpoints";
import type { AuthCtx } from "@/lib/auth/server-api";
import { SYNCS_LIMIT } from "@/lib/discord/params";
import { loadOrNotFound } from "./load";
import { SyncsTable } from "./SyncsTable";

/** The last uploads of the server, newest first. */
export async function SyncsTab({ ctx, guildId }: { ctx: AuthCtx; guildId: string }) {
  const syncs = await loadOrNotFound(getDiscordSyncs(ctx, guildId, SYNCS_LIMIT));
  return (
    <HudPanel label={`ENVOIS · ${syncs.length} DERNIERS`}>
      <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
        Garde-fou : un envoi complet qui ferait partir plus de 25 % des membres est traité comme partiel.
      </p>
      <SyncsTable rows={syncs} />
    </HudPanel>
  );
}
```

- [ ] **Step 19: Run the typecheck, the guard, the whole suite and the build**

Run (from `src/Collector.Web`): `pnpm typecheck`

Expected: exits 0 with no `error TS` line.

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord`

Expected: `Test Files  3 passed (3)` (`format.test.ts` 89, `params.test.ts` 31, `rendering-guard.test.ts` 4). The guard now scans every file of `app/(public)/discord/[guildId]` and the three new files of `components/discord`: none injects HTML, and every Discord name goes through `DiscordText`.

Run (from `src/Collector.Web`): `pnpm test`

Expected: every test file passes (no `FAIL` line). `server-boundary.test.ts` passes: the client components of this task (`GuildAdminActions`, `DiscordMembersTable`, `DiscrepanciesTable`, `SuggestionsList`, `RoleConfigTable`, `SyncsTable`) import types, `@/lib/discord/*` and the server actions only.

Run (from `src/Collector.Web`): `pnpm build`

Expected: `✓ Compiled successfully`, exit code 0; the route table lists `ƒ /discord` and `ƒ /discord/[guildId]`.

- [ ] **Step 20: Commit**

Run from the repository root:

```bash
git add src/Collector.Web/src/lib/discord \
  src/Collector.Web/src/components/discord \
  "src/Collector.Web/src/app/(public)/discord"
git commit -F - <<'EOF'
feat(web): show a Discord server's members, history, RSI gaps, suggestions, config and uploads

/discord/[guildId] gives each server one tab per question, selected by ?tab= so
every view has its own URL: members paged by the API and filtered through the
URL, the history with "Rang : X → Y" and the window between two uploads when
the exact date is unknown, the gaps with the org's RSI roster and its totals,
the link suggestions to validate or ignore (with undo), the corpo and rank
configuration for the responsible user or an admin, and the upload journal
with its guard, partial and baseline badges.

Admins get the erasures and the one-off mass-departure allowance, each behind a
confirmation. URL parameters are whitelisted before they reach the API, and a
server id that is not a snowflake is a not-found page.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task C10: Multi page, user section, org panel, smoke test and README

**Files:**
- Modify: `src/Collector.Web/src/lib/discord/format.ts` (timeline helpers appended after `formatShare`)
- Create: `src/Collector.Web/src/lib/discord/user-profile.ts`
- Create: `src/Collector.Web/src/app/(public)/users/[handle]/DiscordServersSection.tsx`
- Modify: `src/Collector.Web/src/app/(public)/users/[handle]/page.tsx` (imports, new `DiscordServers` wrapper after `Annotations`, the two partial views and the full view)
- Create: `src/Collector.Web/src/app/(public)/orgs/[sid]/OrgDiscordPanel.tsx`
- Modify: `src/Collector.Web/src/app/(public)/orgs/[sid]/page.tsx` (imports, the `Promise.all` block, the panel after `ManualMembersPanel`)
- Create: `src/Collector.Web/src/app/(public)/discord/multi/page.tsx`
- Create: `src/Collector.Web/src/app/(public)/discord/multi/MultiMembersTable.tsx`
- Modify: `src/Collector.Web/tests/e2e/smoke.spec.ts` (`PRIVATE_PAGES`, test "main pages render their frame", new not-found test)
- Modify: `src/Collector.Web/README.md` (table "Pages": rows `/orgs/[sid]` and `/users/[handle]`, new rows after `/changes`)
- Test: `src/Collector.Web/src/lib/discord/format.test.ts` (import list; new `describe` blocks at the end)
- Test: `src/Collector.Web/src/lib/discord/user-profile.test.ts`
- Test: `src/Collector.Web/src/lib/discord/rendering-guard.test.ts` (`DISCORD_UI` gains the two new files)
- Test: `src/Collector.Web/tests/e2e/smoke.spec.ts`

**Interfaces:**
- Consumes:
  - Task C8: types `DiscordUserProfileDto`, `DiscordProfileAccountDto`, `DiscordTimelineEntryDto`, `DiscordOrgGuildDto`, `DiscordMultiMemberDto`; endpoints `getUserDiscord(ctx, handle)`, `getOrgDiscordGuilds(ctx, sid)`, `getDiscordMulti(ctx, { page, pageSize })`; format helpers `discordDisplayName`, `formatUtc`, `syncBadges`, `BadgeSpec`; components `DiscordText`, `GuildIcon`; the guard's `DISCORD_UI` list.
  - Task C9: format helpers `eventTypeLabel`, `eventTone` (handles the RSI `member_joined` / `member_left`); components `RoleChanges`, `ValueChange`, `LinkedPeople`; `SearchParams`, `firstParam` (`@/lib/discord/params`).
  - CONTRACTS § 7: `GET api/users/{handle}/discord` returns empty lists (200) when the handle has no entity and 404 when the handle is unknown everywhere; the timeline holds at most 100 entries, newest first, each marked `rsi` or `discord`, with `notBefore` when the exact date is unknown; `GET api/organizations/{sid}/discord` returns `[]` when no server is mapped; `GET api/discord/multi` is a `PaginatedResponse<DiscordMultiMemberDto>`.
  - Existing: `getSession`, `sessionCtx`, `requireAuthCtx`, `withAuthRedirect`, `ApiError`, `parsePage`, `Pagination`, `HudPanel`, `HudBadge`, `HudDataGrid`, `formatNumber`; the user page's `Annotations` / `PartialUserProfile` branches; the org page's `Promise.all`; `PRIVATE_PAGES` and `signIn` in `smoke.spec.ts`.
- Produces:
  - `format.ts` additions: `timelineSourceBadge(source)`, `timelineTypeLabel({ source, type })`, `timelineWhen({ at, notBefore })`.
  - `src/lib/discord/user-profile.ts`: `UserDiscordState = { kind: "profile"; profile } | { kind: "empty" } | { kind: "unavailable" }` and `loadUserDiscord(ctx, handle): Promise<UserDiscordState>` (404 and "no linked account" → `empty`; any other failure → `unavailable`; never throws).
  - `DiscordServersSection({ handle })` (async server component, panel "DISCORD · SERVEURS"), rendered through Suspense on every view of `/users/[handle]`; `OrgDiscordPanel({ guilds })` (hidden when empty) on `/orgs/[sid]`; page `/discord/multi` with client `MultiMembersTable({ rows })`.
  - Smoke test: `PRIVATE_PAGES` includes `"/discord"` and `"/discord/multi"`; the web README lists the three Discord pages.

- [ ] **Step 1: Write the failing timeline helper tests**

In `src/Collector.Web/src/lib/discord/format.test.ts`, replace:

```ts
  rolesDiff,
  safeRoleColor,
  syncBadges,
} from "./format";
```

with:

```ts
  rolesDiff,
  safeRoleColor,
  syncBadges,
  timelineSourceBadge,
  timelineTypeLabel,
  timelineWhen,
} from "./format";
```

Then append at the end of the file:

```ts

describe("timelineSourceBadge", () => {
  it.each([
    ["rsi", "RSI", "cyan"],
    ["discord", "DISCORD", "dim"],
  ] as const)("marks a %s entry", (source, label, tone) => {
    expect(timelineSourceBadge(source)).toEqual({ label, tone });
  });
});

describe("timelineTypeLabel", () => {
  it.each([
    ["rsi", "member_joined", "arrivée dans l'org"],
    ["rsi", "member_left", "départ de l'org"],
    ["rsi", "rank_changed", "rang RSI"],
    ["rsi", "roles_changed", "rôles RSI"],
    ["rsi", "handle_changed", "changement de handle"],
    ["rsi", "org_renamed", "org renamed"],
    ["discord", "joined", "arrivée"],
    ["discord", "roles_changed", "rôles"],
  ] as const)("%s %s → « %s »", (source, type, label) => {
    expect(timelineTypeLabel({ source, type })).toBe(label);
  });
});

describe("timelineWhen", () => {
  it("gives the date of an entry whose date is exact", () => {
    expect(timelineWhen({ at: "2026-09-29T08:30:00Z", notBefore: null })).toBe("2026-09-29 08:30 UTC");
  });

  it("gives the window of a Discord event whose exact date is unknown", () => {
    expect(timelineWhen({ at: "2026-09-30T12:00:00Z", notBefore: "2026-09-28T20:00:00Z" })).toBe(
      "entre 2026-09-28 20:00 UTC et 2026-09-30 12:00 UTC",
    );
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/format.test.ts`

Expected: `FAIL  src/lib/discord/format.test.ts`; the 12 new tests fail with `TypeError: timelineSourceBadge is not a function` (or `timelineTypeLabel` / `timelineWhen`); the 89 earlier tests pass.

- [ ] **Step 3: Write the minimal implementation (timeline helpers)**

Append at the end of `src/Collector.Web/src/lib/discord/format.ts`:

```ts

const RSI_CHANGE_LABELS = new Map<string, string>([
  ["member_joined", "arrivée dans l'org"],
  ["member_left", "départ de l'org"],
  ["rank_changed", "rang RSI"],
  ["roles_changed", "rôles RSI"],
  ["handle_changed", "changement de handle"],
]);

/** Where a timeline entry comes from (spec § 10.4): the RSI history or a Discord server. */
export function timelineSourceBadge(source: "rsi" | "discord"): BadgeSpec {
  return source === "rsi" ? { label: "RSI", tone: "cyan" } : { label: "DISCORD", tone: "dim" };
}

/** French name of a timeline entry: RSI change types, or Discord event types. */
export function timelineTypeLabel(entry: { source: "rsi" | "discord"; type: string }): string {
  if (entry.source === "discord") return eventTypeLabel(entry.type);
  return RSI_CHANGE_LABELS.get(entry.type) ?? entry.type.replace(/_/g, " ");
}

/** When a timeline entry happened: its date, or the window when only an upper bound is known. */
export function timelineWhen(entry: { at: string; notBefore: string | null }): string {
  return entry.notBefore ? `entre ${formatUtc(entry.notBefore)} et ${formatUtc(entry.at)}` : formatUtc(entry.at);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/format.test.ts`

Expected: `Test Files  1 passed (1)`, `Tests  101 passed (101)`.

- [ ] **Step 5: Write the failing cross-profile loader tests (review focus 5)**

Create `src/Collector.Web/src/lib/discord/user-profile.test.ts`:

```ts
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DiscordTimelineEntryDto, DiscordUserProfileDto } from "@/lib/api/types";
import { loadUserDiscord } from "./user-profile";

const ctx = { bearerToken: "t" };
const GUILD = "123456789012345678";

const discordJoin: DiscordTimelineEntryDto = {
  source: "discord",
  type: "joined",
  at: "2025-03-14T20:11:05Z",
  notBefore: null,
  orgSid: "CORP",
  guildId: GUILD,
  guildName: "Ma Corpo",
  oldValue: null,
  newValue: "2025-03-14T20:11:05Z",
};

const rsiJoin: DiscordTimelineEntryDto = {
  source: "rsi",
  type: "member_joined",
  at: "2025-01-02T10:00:00Z",
  notBefore: null,
  orgSid: "CORP",
  guildId: null,
  guildName: null,
  oldValue: null,
  newValue: null,
};

const profile: DiscordUserProfileDto = {
  accounts: [
    {
      discordUserId: "323456789012345678",
      username: "pilote42",
      globalName: "Pilote",
      guilds: [
        {
          guildId: GUILD,
          guildName: "Ma Corpo",
          orgSid: "CORP",
          rank: "Officier",
          joinedAt: "2025-03-14T20:11:05Z",
          leftAt: null,
          lastSeenAt: "2026-09-30T12:00:00Z",
        },
      ],
    },
  ],
  timeline: [discordJoin, rsiJoin],
};

function stubFetch(status: number, body: unknown) {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), { status }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("loadUserDiscord", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("returns the cross profile of a citizen with linked accounts", async () => {
    const fetchMock = stubFetch(200, profile);

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "profile", profile });
    const [url] = fetchMock.mock.calls[0] as unknown as [string];
    expect(new URL(url).pathname).toBe("/api/users/Pilote42/discord");
  });

  it("gives the empty state for a citizen without Discord data (200 with empty lists)", async () => {
    stubFetch(200, { accounts: [], timeline: [] });

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "empty" });
  });

  it("gives the empty state when there are RSI entries but no linked account", async () => {
    stubFetch(200, { accounts: [], timeline: [rsiJoin] });

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "empty" });
  });

  it("gives the empty state for a handle the API does not know (404) instead of failing the page", async () => {
    stubFetch(404, { title: "Not Found", status: 404 });

    expect(await loadUserDiscord(ctx, "NoSuchCitizen")).toEqual({ kind: "empty" });
  });

  it("reports an API failure as unavailable instead of throwing", async () => {
    stubFetch(500, { title: "Internal Server Error", status: 500 });

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "unavailable" });
  });

  it("reports an unreachable API as unavailable", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new TypeError("fetch failed");
      }),
    );

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "unavailable" });
  });
});
```

- [ ] **Step 6: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/user-profile.test.ts`

Expected: `FAIL  src/lib/discord/user-profile.test.ts` before any test runs, with `Error: Cannot find module './user-profile'` (or Vite's `Failed to load url ./user-profile`).

- [ ] **Step 7: Write the minimal implementation (cross-profile loader)**

Create `src/Collector.Web/src/lib/discord/user-profile.ts`:

```ts
/**
 * Loads a citizen's Discord cross profile for the « DISCORD · SERVEURS » section. The
 * section must never break the citizen page: a handle the API does not know (404) or a
 * citizen without a linked Discord account gives the empty state, any other failure a
 * short notice. Server-side only (it calls the API client).
 */

import { getUserDiscord } from "@/lib/api/endpoints";
import { ApiError } from "@/lib/api/errors";
import type { DiscordUserProfileDto } from "@/lib/api/types";

export type UserDiscordState =
  | { kind: "profile"; profile: DiscordUserProfileDto }
  | { kind: "empty" }
  | { kind: "unavailable" };

export async function loadUserDiscord(
  ctx: Parameters<typeof getUserDiscord>[0],
  handle: string,
): Promise<UserDiscordState> {
  try {
    const profile = await getUserDiscord(ctx, handle);
    const accounts = Array.isArray(profile?.accounts) ? profile.accounts : [];
    // Without a linked account the timeline would only repeat the RSI changes of the page.
    if (accounts.length === 0) return { kind: "empty" };
    return {
      kind: "profile",
      profile: { accounts, timeline: Array.isArray(profile.timeline) ? profile.timeline : [] },
    };
  } catch (err) {
    return err instanceof ApiError && err.status === 404 ? { kind: "empty" } : { kind: "unavailable" };
  }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/user-profile.test.ts`

Expected: `Test Files  1 passed (1)`, `Tests  6 passed (6)`.

- [ ] **Step 9: Extend the rendering guard to the new user and org components (review focus 1)**

In `src/Collector.Web/src/lib/discord/rendering-guard.test.ts`, replace:

```ts
const DISCORD_UI = [
  "app/(public)/discord",
  "components/discord",
  "lib/discord",
];
```

with:

```ts
const DISCORD_UI = [
  "app/(public)/discord",
  "components/discord",
  "lib/discord",
  "app/(public)/users/[handle]/DiscordServersSection.tsx",
  "app/(public)/orgs/[sid]/OrgDiscordPanel.tsx",
];
```

- [ ] **Step 10: Run it to verify it fails**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/rendering-guard.test.ts`

Expected: `FAIL`; `scans paths that exist` reports `["app/(public)/users/[handle]/DiscordServersSection.tsx", "app/(public)/orgs/[sid]/OrgDiscordPanel.tsx"]` instead of `[]`; the three other tests pass.

- [ ] **Step 11: Wire the section and the panel into their pages (the consumers first)**

In `src/Collector.Web/src/app/(public)/users/[handle]/page.tsx`, replace:

```tsx
import { UserAnnotations } from "./UserAnnotations";
```

with:

```tsx
import { UserAnnotations } from "./UserAnnotations";
import { DiscordServersSection } from "./DiscordServersSection";
```

Then replace:

```tsx
      <UserAnnotations {...props} />
    </Suspense>
  );
}
```

with:

```tsx
      <UserAnnotations {...props} />
    </Suspense>
  );
}

/** The Discord cross profile makes its own API call: the rest of the page renders first. */
function DiscordServers({ handle }: { handle: string }) {
  return (
    <Suspense
      fallback={
        <HudPanel label="DISCORD · SERVEURS">
          <div className="py-6 text-center font-mono text-xs text-hud-text-dim">— loading —</div>
        </HudPanel>
      }
    >
      <DiscordServersSection handle={handle} />
    </Suspense>
  );
}
```

Then replace:

```tsx
            <PartialUserProfile handle={handle} orgs={knownOrgs} />
            <Annotations handle={handle} canSetCitizenId />
```

with:

```tsx
            <PartialUserProfile handle={handle} orgs={knownOrgs} />
            <DiscordServers handle={handle} />
            <Annotations handle={handle} canSetCitizenId />
```

Then replace:

```tsx
            <PartialUserProfile handle={handle} orgs={[]} />
            <Annotations handle={handle} canSetCitizenId />
```

with:

```tsx
            <PartialUserProfile handle={handle} orgs={[]} />
            <DiscordServers handle={handle} />
            <Annotations handle={handle} canSetCitizenId />
```

Then replace:

```tsx
      <Annotations handle={handle} canSetCitizenId={!user.citizenId} />
```

with:

```tsx
      <DiscordServers handle={handle} />

      <Annotations handle={handle} canSetCitizenId={!user.citizenId} />
```

In `src/Collector.Web/src/app/(public)/orgs/[sid]/page.tsx`, replace:

```tsx
import {
  getOrg,
  getOrgGrowth,
  getOrgMemberChanges,
  getOrgMembersPage,
} from "@/lib/api/endpoints";
import { ApiError } from "@/lib/api/errors";
import type { OrganizationMemberDto, PaginatedResponse } from "@/lib/api/types";
```

with:

```tsx
import {
  getOrg,
  getOrgDiscordGuilds,
  getOrgGrowth,
  getOrgMemberChanges,
  getOrgMembersPage,
} from "@/lib/api/endpoints";
import { ApiError } from "@/lib/api/errors";
import type { DiscordOrgGuildDto, OrganizationMemberDto, PaginatedResponse } from "@/lib/api/types";
```

Then replace:

```tsx
import { ManualMembersPanel, type OrgManualMember } from "./ManualMembersPanel";
```

with:

```tsx
import { ManualMembersPanel, type OrgManualMember } from "./ManualMembersPanel";
import { OrgDiscordPanel } from "./OrgDiscordPanel";
```

Then replace:

```tsx
  const [session, members, formerMembers, growth, changes, notes, manualMembers] =
    await Promise.all([
      getSession(),
      getOrgMembersPage(sid, { status: "active", page: activePage, pageSize: MEMBERS_PAGE_SIZE }, ctx)
        .catch(() => noMembers(activePage)),
      getOrgMembersPage(sid, { status: "former", page: formerPage, pageSize: MEMBERS_PAGE_SIZE }, ctx)
        .catch(() => noMembers(formerPage)),
      getOrgGrowth(sid, ctx).catch(() => []),
      getOrgMemberChanges(sid, 20, ctx).catch(() => []),
      apiGet<OrgNoteDto[]>(`${orgPath}/notes`, undefined, ctx).catch(() => [] as OrgNoteDto[]),
      apiGet<OrgManualMember[]>(`${orgPath}/manual-members`, undefined, ctx)
        .catch(() => [] as OrgManualMember[]),
    ]);
```

with:

```tsx
  const [session, members, formerMembers, growth, changes, notes, manualMembers, discordGuilds] =
    await Promise.all([
      getSession(),
      getOrgMembersPage(sid, { status: "active", page: activePage, pageSize: MEMBERS_PAGE_SIZE }, ctx)
        .catch(() => noMembers(activePage)),
      getOrgMembersPage(sid, { status: "former", page: formerPage, pageSize: MEMBERS_PAGE_SIZE }, ctx)
        .catch(() => noMembers(formerPage)),
      getOrgGrowth(sid, ctx).catch(() => []),
      getOrgMemberChanges(sid, 20, ctx).catch(() => []),
      apiGet<OrgNoteDto[]>(`${orgPath}/notes`, undefined, ctx).catch(() => [] as OrgNoteDto[]),
      apiGet<OrgManualMember[]>(`${orgPath}/manual-members`, undefined, ctx)
        .catch(() => [] as OrgManualMember[]),
      // Discord servers mapped to this org; the panel stays hidden when there is none.
      getOrgDiscordGuilds(ctx, sid).catch(() => [] as DiscordOrgGuildDto[]),
    ]);
```

Then replace:

```tsx
      <ManualMembersPanel members={manualMembers} />
```

with:

```tsx
      <ManualMembersPanel members={manualMembers} />

      <OrgDiscordPanel guilds={discordGuilds} />
```

- [ ] **Step 12: Run the typecheck to verify it fails**

Run (from `src/Collector.Web`): `pnpm typecheck`

Expected: `error TS2307: Cannot find module './DiscordServersSection' or its corresponding type declarations.` in `src/app/(public)/users/[handle]/page.tsx` and `error TS2307: Cannot find module './OrgDiscordPanel' …` in `src/app/(public)/orgs/[sid]/page.tsx`.

- [ ] **Step 13: Write the citizen section and the org panel**

Create `src/Collector.Web/src/app/(public)/users/[handle]/DiscordServersSection.tsx`:

```tsx
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { RoleChanges } from "@/components/discord/RoleChanges";
import { ValueChange } from "@/components/discord/ValueChange";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudPanel } from "@/components/hud/HudPanel";
import type { DiscordProfileAccountDto, DiscordTimelineEntryDto } from "@/lib/api/types";
import { getSession, sessionCtx } from "@/lib/auth/session";
import {
  discordDisplayName,
  eventTone,
  formatUtc,
  timelineSourceBadge,
  timelineTypeLabel,
  timelineWhen,
} from "@/lib/discord/format";
import { loadUserDiscord } from "@/lib/discord/user-profile";

/** Entries whose values are dates: their type says it all. */
const NO_VALUE_TYPES = new Set(["joined", "left", "rejoined", "member_joined", "member_left"]);

function AccountServers({ account }: { account: DiscordProfileAccountDto }) {
  return (
    <li className="flex flex-col gap-2 border-b border-hud-cyan/10 pb-3 last:border-0 last:pb-0">
      <div className="flex min-w-0 flex-wrap items-baseline gap-2 font-mono text-sm">
        <span className="inline-flex min-w-0 max-w-full text-hud-text">
          <DiscordText value={discordDisplayName(account)} />
        </span>
        <span className="inline-flex min-w-0 max-w-[16rem] text-[11px] text-hud-text-dim">
          @<DiscordText value={account.username} />
        </span>
        <span className="text-[10px] text-hud-text-dim">{account.discordUserId}</span>
      </div>
      {account.guilds.length === 0 ? (
        <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">Aucun serveur suivi.</p>
      ) : (
        <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
          {account.guilds.map((guild) => (
            <li key={guild.guildId} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-1.5">
              <span className="flex min-w-0 flex-1 items-center gap-2">
                <Link
                  href={`/discord/${encodeURIComponent(guild.guildId)}`}
                  className="inline-flex min-w-0 max-w-[20rem] text-hud-cyan hover:text-hud-orange"
                >
                  <DiscordText value={guild.guildName} fallback={guild.guildId} />
                </Link>
                {guild.orgSid && (
                  <Link
                    href={`/orgs/${encodeURIComponent(guild.orgSid)}`}
                    className="shrink-0 text-hud-text-dim hover:text-hud-cyan"
                  >
                    [{guild.orgSid}]
                  </Link>
                )}
                {guild.rank && (
                  <span className="inline-flex min-w-0 max-w-[12rem] text-hud-text">
                    <DiscordText value={guild.rank} />
                  </span>
                )}
              </span>
              <span className="flex items-center gap-2 text-[10px] uppercase tracking-wide text-hud-text-dim">
                <span>arrivée {formatUtc(guild.joinedAt)}</span>
                {guild.leftAt ? (
                  <>
                    <HudBadge tone="red">PARTI</HudBadge>
                    <span>{formatUtc(guild.leftAt)}</span>
                  </>
                ) : (
                  <HudBadge tone="green">PRÉSENT</HudBadge>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}

function TimelinePlace({ entry }: { entry: DiscordTimelineEntryDto }) {
  if (entry.source === "discord" && entry.guildId) {
    return (
      <Link
        href={`/discord/${encodeURIComponent(entry.guildId)}`}
        className="inline-flex min-w-0 max-w-[16rem] text-hud-cyan hover:text-hud-orange"
      >
        <DiscordText value={entry.guildName} fallback={entry.guildId} />
      </Link>
    );
  }
  if (entry.orgSid) {
    return (
      <Link href={`/orgs/${encodeURIComponent(entry.orgSid)}`} className="text-hud-cyan hover:text-hud-orange">
        {entry.orgSid}
      </Link>
    );
  }
  return null;
}

function TimelineDetail({ entry }: { entry: DiscordTimelineEntryDto }) {
  if (entry.type === "roles_changed") {
    // Discord values carry [{id,name}]; RSI ones are raw role labels, unreadable inline.
    return entry.source === "discord" ? <RoleChanges oldValue={entry.oldValue} newValue={entry.newValue} /> : null;
  }
  if (NO_VALUE_TYPES.has(entry.type) || (entry.oldValue === null && entry.newValue === null)) return null;
  return <ValueChange oldValue={entry.oldValue} newValue={entry.newValue} />;
}

/**
 * « DISCORD · SERVEURS » (spec § 10.4): the Discord accounts linked to the citizen, the
 * servers each is or was on, and one timeline of RSI and Discord events. Distinct from
 * DiscordPanel, which shows the public profile of a manually added Discord id.
 */
export async function DiscordServersSection({ handle }: { handle: string }) {
  const session = await getSession();
  if (!session) return null;
  const state = await loadUserDiscord(sessionCtx(session), handle);

  return (
    <HudPanel label="DISCORD · SERVEURS">
      {state.kind === "unavailable" ? (
        <p className="py-4 text-center font-mono text-xs text-hud-red">Données Discord indisponibles.</p>
      ) : state.kind === "empty" ? (
        <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
          {"Aucun compte Discord lié à ce citoyen. Les liens se valident dans l'onglet Suggestions d'un serveur Discord."}
        </p>
      ) : (
        <div className="flex flex-col gap-6">
          <ul className="flex flex-col gap-4">
            {state.profile.accounts.map((account) => (
              <AccountServers key={account.discordUserId} account={account} />
            ))}
          </ul>
          {state.profile.timeline.length > 0 && (
            <div className="flex flex-col gap-2">
              <div className="hud-label text-hud-text-dim">FRISE RSI + DISCORD</div>
              <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
                {state.profile.timeline.map((entry, i) => {
                  const source = timelineSourceBadge(entry.source);
                  return (
                    <li
                      key={`${entry.source}-${entry.type}-${entry.at}-${i}`}
                      className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2"
                    >
                      <span className="flex min-w-0 flex-1 flex-wrap items-center gap-2">
                        <HudBadge tone={source.tone}>{source.label}</HudBadge>
                        <HudBadge tone={eventTone(entry.type)}>{timelineTypeLabel(entry)}</HudBadge>
                        <TimelinePlace entry={entry} />
                        <TimelineDetail entry={entry} />
                      </span>
                      <time className="text-[10px] text-hud-text-dim">{timelineWhen(entry)}</time>
                    </li>
                  );
                })}
              </ul>
            </div>
          )}
        </div>
      )}
    </HudPanel>
  );
}
```

Create `src/Collector.Web/src/app/(public)/orgs/[sid]/OrgDiscordPanel.tsx`:

```tsx
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudPanel } from "@/components/hud/HudPanel";
import type { DiscordOrgGuildDto } from "@/lib/api/types";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { formatNumber } from "@/lib/utils/format";

/** Discord servers mapped to this org (principal, recruitment…); hidden when there is none. */
export function OrgDiscordPanel({ guilds }: { guilds: DiscordOrgGuildDto[] }) {
  if (guilds.length === 0) return null;

  return (
    <HudPanel label={guilds.length > 1 ? `DISCORD · ${guilds.length} SERVEURS` : "DISCORD"}>
      <ul className="flex flex-col divide-y divide-hud-cyan/10">
        {guilds.map((guild) => (
          <li
            key={guild.guildId}
            className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2 font-mono text-sm"
          >
            <Link
              href={`/discord/${encodeURIComponent(guild.guildId)}`}
              className="flex min-w-0 flex-1 items-center gap-3 text-hud-cyan hover:text-hud-orange"
            >
              <GuildIcon guildId={guild.guildId} iconHash={guild.iconHash} />
              <DiscordText value={guild.name} fallback={guild.guildId} className="min-w-0" />
            </Link>
            <span className="flex flex-wrap items-center gap-2 text-[10px] uppercase tracking-wide text-hud-text-dim">
              <span>
                {formatNumber(guild.activeMembers)} actifs · {formatNumber(guild.linkedMembers)} liés
              </span>
              {syncBadges({ isComplete: guild.lastSyncComplete, departureGuardTripped: false }).map((b) => (
                <HudBadge key={b.label} tone={b.tone}>
                  {b.label}
                </HudBadge>
              ))}
              <span>{formatUtc(guild.lastSyncAt)}</span>
            </span>
          </li>
        ))}
      </ul>
    </HudPanel>
  );
}
```

- [ ] **Step 14: Run the guard and the typecheck to verify they pass**

Run (from `src/Collector.Web`): `pnpm exec vitest run src/lib/discord/rendering-guard.test.ts`

Expected: `Test Files  1 passed (1)`, `Tests  4 passed (4)`: both new files exist, neither injects HTML, and both render Discord names through `DiscordText`.

Run (from `src/Collector.Web`): `pnpm typecheck`

Expected: exits 0 with no `error TS` line.

- [ ] **Step 15: Write the multi-membership page**

Create `src/Collector.Web/src/app/(public)/discord/multi/MultiMembersTable.tsx`:

```tsx
"use client";
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { LinkedPeople } from "@/components/discord/LinkedPeople";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordMultiMemberDto } from "@/lib/api/types";
import { discordDisplayName } from "@/lib/discord/format";

/** One API page of accounts present on at least two tracked servers (spec § 10.3). */
export function MultiMembersTable({ rows }: { rows: DiscordMultiMemberDto[] }) {
  const columns: HudColumn<DiscordMultiMemberDto>[] = [
    {
      key: "account",
      header: "COMPTE",
      width: "w-56 min-w-0",
      render: (m) => (
        <span className="flex min-w-0 flex-col">
          <span className="inline-flex min-w-0 max-w-full text-hud-text">
            <DiscordText value={discordDisplayName(m)} />
          </span>
          <span className="inline-flex min-w-0 max-w-full text-[10px] text-hud-text-dim">
            @<DiscordText value={m.username} />
          </span>
        </span>
      ),
    },
    {
      key: "guilds",
      header: "SERVEURS",
      width: "min-w-0 flex-1",
      render: (m) => (
        <ul className="flex min-w-0 flex-col gap-0.5">
          {m.guilds.map((guild) => (
            <li key={guild.guildId} className="flex min-w-0 items-center gap-2">
              <Link
                href={`/discord/${encodeURIComponent(guild.guildId)}`}
                className="inline-flex min-w-0 max-w-[16rem] text-hud-cyan hover:text-hud-orange"
              >
                <DiscordText value={guild.guildName} fallback={guild.guildId} />
              </Link>
              {guild.orgSid ? (
                <Link
                  href={`/orgs/${encodeURIComponent(guild.orgSid)}`}
                  className="shrink-0 text-hud-text-dim hover:text-hud-cyan"
                >
                  [{guild.orgSid}]
                </Link>
              ) : (
                <span className="shrink-0 text-[10px] text-hud-text-dim">non relié</span>
              )}
              {guild.rank && (
                <span className="inline-flex min-w-0 max-w-[10rem] text-hud-text">
                  <DiscordText value={guild.rank} />
                </span>
              )}
            </li>
          ))}
        </ul>
      ),
    },
    {
      key: "links",
      header: "CITOYENS LIÉS",
      width: "w-40 min-w-0",
      render: (m) => <LinkedPeople links={m.links} />,
    },
    {
      key: "rsiOrgs",
      header: "ORGS RSI",
      width: "w-48 min-w-0",
      render: (m) =>
        m.rsiOrgs.length === 0 ? (
          <span className="text-hud-text-dim">—</span>
        ) : (
          <span className="flex min-w-0 flex-col gap-0.5">
            {m.rsiOrgs.map((org) => (
              <span key={org.sid} className="flex min-w-0 items-center gap-2">
                <Link
                  href={`/orgs/${encodeURIComponent(org.sid)}`}
                  className="shrink-0 text-hud-cyan hover:text-hud-orange"
                >
                  {org.sid}
                </Link>
                {org.rank && <span className="truncate text-hud-text-dim">{org.rank}</span>}
              </span>
            ))}
          </span>
        ),
    },
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(m) => m.discordUserId}
      empty="Aucun compte n'est présent sur au moins deux serveurs suivis."
    />
  );
}
```

Create `src/Collector.Web/src/app/(public)/discord/multi/page.tsx`:

```tsx
import Link from "next/link";
import { HudPanel } from "@/components/hud/HudPanel";
import { Pagination } from "@/components/layout/Pagination";
import { getDiscordMulti } from "@/lib/api/endpoints";
import { requireAuthCtx, withAuthRedirect } from "@/lib/auth/server-api";
import { firstParam, type SearchParams } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { parsePage } from "@/lib/utils/page-param";
import { MultiMembersTable } from "./MultiMembersTable";

export const dynamic = "force-dynamic";

/** Accounts per page, paged by the API. */
const PAGE_SIZE = 50;

/** Discord accounts present on several tracked servers, with the orgs on both sides. */
export default async function DiscordMultiPage({ searchParams }: { searchParams: Promise<SearchParams> }) {
  const sp = await searchParams;
  const page = parsePage(firstParam(sp.page));
  const ctx = await requireAuthCtx();
  const data = await withAuthRedirect(getDiscordMulti(ctx, { page, pageSize: PAGE_SIZE }));

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <div className="hud-label">— UEE::DISCORD_MULTI</div>
          <h1 className="mt-1 font-display text-3xl">Multi-appartenance</h1>
          <p className="mt-1 max-w-3xl font-mono text-xs text-hud-text-dim">
            {"Comptes Discord présents sur au moins deux serveurs suivis (hors bots), avec la corpo de chaque serveur et, pour les comptes liés, les orgs RSI actives des citoyens."}
          </p>
        </div>
        <Link
          href="/discord"
          className="hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10"
        >
          ← SERVEURS
        </Link>
      </header>

      <HudPanel label={`${formatNumber(data.total)} COMPTES`}>
        <MultiMembersTable rows={data.items} />
        {data.totalPages > 1 && (
          <Pagination param="page" page={data.page} totalPages={data.totalPages} total={data.total} />
        )}
      </HudPanel>
    </div>
  );
}
```

- [ ] **Step 16: Write the failing documentation and smoke-test check**

The check, run from the repository root in Git Bash:

```bash
check() { if grep -qF -- "$2" "$1"; then echo "ok      $1: $2"; else echo "MISSING $1: $2"; fi; }
check src/Collector.Web/tests/e2e/smoke.spec.ts '"/orgs/TEST", "/discord", "/discord/multi"'
check src/Collector.Web/tests/e2e/smoke.spec.ts 'UEE::DISCORD_ROSTERS'
check src/Collector.Web/tests/e2e/smoke.spec.ts 'an unknown Discord server gets the not-found page'
check src/Collector.Web/README.md '| `/discord` |'
check src/Collector.Web/README.md '| `/discord/[guildId]` |'
check src/Collector.Web/README.md '| `/discord/multi` |'
check src/Collector.Web/README.md 'notes, serveurs Discord reliés |'
check src/Collector.Web/README.md 'annotations, serveurs Discord et frise RSI + Discord |'
```

- [ ] **Step 17: Run it to verify it fails**

Run the Step 16 script.

Expected: 8 lines, every one starting with `MISSING`.

- [ ] **Step 18: Update the smoke test and the web README**

In `src/Collector.Web/tests/e2e/smoke.spec.ts`, replace:

```ts
const PRIVATE_PAGES = ["/", "/orgs", "/users", "/stats", "/changes", "/dashboard", "/orgs/TEST"];
```

with:

```ts
const PRIVATE_PAGES = ["/", "/orgs", "/users", "/stats", "/changes", "/dashboard", "/orgs/TEST", "/discord", "/discord/multi"];
```

Then replace:

```ts
    await page.goto("/changes");
    await expect(page.getByText(/UEE::LIVE_CHANGELOG/)).toBeVisible();
    await page.goto("/dashboard");
    expect(new URL(page.url()).pathname).toBe("/dashboard");
  });
```

with:

```ts
    await page.goto("/changes");
    await expect(page.getByText(/UEE::LIVE_CHANGELOG/)).toBeVisible();
    await page.goto("/discord");
    await expect(page.getByText(/UEE::DISCORD_ROSTERS/)).toBeVisible();
    await page.goto("/discord/multi");
    await expect(page.getByText(/UEE::DISCORD_MULTI/)).toBeVisible();
    await page.goto("/dashboard");
    expect(new URL(page.url()).pathname).toBe("/dashboard");
  });

  test("an unknown Discord server gets the not-found page", async ({ page }) => {
    await signIn(page);
    // Not a snowflake: the page answers without calling the API.
    await page.goto("/discord/NOT_A_SERVER");
    await expect(page.getByRole("heading", { name: /Not found/i })).toBeVisible();
  });
```

In `src/Collector.Web/README.md`, replace:

```markdown
| `/orgs/[sid]` | Fiche organisation : membres actuels et anciens paginés, croissance, activité, notes |
```

with:

```markdown
| `/orgs/[sid]` | Fiche organisation : membres actuels et anciens paginés, croissance, activité, notes, serveurs Discord reliés |
```

Then replace:

```markdown
| `/users/[handle]` | Profil : organisations, historique des handles, changements, annotations |
```

with:

```markdown
| `/users/[handle]` | Profil : organisations, historique des handles, changements, annotations, serveurs Discord et frise RSI + Discord |
```

Then replace:

```markdown
| `/changes` | Flux des changements, rafraîchi toutes les 30 s |
```

with:

```markdown
| `/changes` | Flux des changements, rafraîchi toutes les 30 s |
| `/discord` | Serveurs Discord envoyés par le plugin Vencord : corpo reliée, actifs, rangs, dernier envoi ; serveurs non reliés en tête, avec leur SID à saisir |
| `/discord/[guildId]` | Serveur Discord par onglets : membres, historique, écarts RSI, suggestions de liens, configuration des rangs, envois ; effacements réservés aux admins |
| `/discord/multi` | Comptes Discord présents sur plusieurs serveurs suivis, avec les orgs RSI des citoyens liés |
```

- [ ] **Step 19: Run the check and the anonymous smoke tests to verify they pass**

Run the Step 16 script from the repository root.

Expected: 8 lines, every one starting with `ok`.

Run (from `src/Collector.Web`): `pnpm exec playwright test --grep "anonymous visitor"`

Expected: Playwright starts `pnpm dev` (no `E2E_BASE_URL`), then `12 passed`, among them `/discord redirects to the login page` and `/discord/multi redirects to the login page`. If Chromium is missing, run `pnpm exec playwright install chromium` once first. The signed-in tests (including the new not-found one) run only with `E2E_USERNAME` / `E2E_PASSWORD` and are otherwise skipped.

- [ ] **Step 20: Run the whole suite and the build**

Run (from `src/Collector.Web`): `pnpm test`

Expected: every test file passes (no `FAIL` line): `src/lib/discord/*` (`format.test.ts` 101, `params.test.ts` 31, `user-profile.test.ts` 6, `rendering-guard.test.ts` 4), and `server-boundary.test.ts` (`MultiMembersTable` imports types, `@/lib/discord/format` and components only).

Run (from `src/Collector.Web`): `pnpm build`

Expected: `✓ Compiled successfully`, exit code 0; the route table lists `ƒ /discord`, `ƒ /discord/[guildId]` and `ƒ /discord/multi`.

- [ ] **Step 21: Commit**

Run from the repository root:

```bash
git add src/Collector.Web/src/lib/discord \
  "src/Collector.Web/src/app/(public)/discord/multi" \
  ":(literal)src/Collector.Web/src/app/(public)/users/[handle]/DiscordServersSection.tsx" \
  ":(literal)src/Collector.Web/src/app/(public)/users/[handle]/page.tsx" \
  ":(literal)src/Collector.Web/src/app/(public)/orgs/[sid]/OrgDiscordPanel.tsx" \
  ":(literal)src/Collector.Web/src/app/(public)/orgs/[sid]/page.tsx" \
  src/Collector.Web/tests/e2e/smoke.spec.ts \
  src/Collector.Web/README.md
git commit -F - <<'EOF'
feat(web): add Discord multi-membership, citizen and org views

/discord/multi pages the accounts present on several tracked servers, with the
org of each server and the RSI orgs of the linked citizens. The citizen page
gains « DISCORD · SERVEURS »: the linked accounts, the servers each is or was
on, and one timeline of RSI and Discord events. It loads on its own and never
breaks the page: no linked account or a handle the API does not know shows the
empty state, any other failure a short notice. The org page lists its mapped
servers and hides the panel when there is none.

The inert-text guard now covers the two new components, the smoke test checks
that the Discord pages stay private, and the web README lists them.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```
