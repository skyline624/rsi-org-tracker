/**
 * Fonctions typées par endpoint. Importées depuis les pages RSC et les hooks
 * TanStack Query. Le serveur passe le JWT de l'utilisateur (`{ bearerToken }`).
 */

import { apiGet, apiPost } from "./client";
import type {
  ApiKeyDto,
  ArchetypeStatsDto,
  AuthResponse,
  ChangeEventDto,
  ChangeSummaryDto,
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
  LoginRequest,
  MemberActivityDto,
  OrganizationDto,
  OrganizationMemberDto,
  OrganizationTopDto,
  PaginatedResponse,
  StatsOverviewDto,
  TimelinePointDto,
  UserDto,
  UserHandleHistoryDto,
  UserProfileDto,
} from "./types";

type Ctx = { bearerToken?: string; clientIp?: string };

// ── Health ──────────────────────────────────────────────────
export const getCycleStatus = (ctx: Ctx = {}) =>
  apiGet<CycleStatusDto>("/api/health/cycle", undefined, ctx);

// ── Stats ───────────────────────────────────────────────────
export const getStatsOverview = (ctx: Ctx = {}) =>
  apiGet<StatsOverviewDto>("/api/stats", undefined, ctx);
export const getStatsTimeline = (days = 30, ctx: Ctx = {}) =>
  apiGet<TimelinePointDto[]>("/api/stats/timeline", { days }, ctx);
export const getTopOrgs = (limit = 10, ctx: Ctx = {}) =>
  apiGet<OrganizationTopDto[]>(
    "/api/stats/organizations/top",
    { limit },
    ctx,
  );
export const getArchetypeStats = (ctx: Ctx = {}) =>
  apiGet<ArchetypeStatsDto[]>(
    "/api/stats/organizations/archetypes",
    undefined,
    ctx,
  );
export const getMemberActivity = (days = 30, ctx: Ctx = {}) =>
  apiGet<MemberActivityDto[]>(
    "/api/stats/members/activity",
    { days },
    ctx,
  );

// ── Organizations ───────────────────────────────────────────
export interface OrgListQuery {
  search?: string;
  archetype?: string;
  commitment?: string;
  lang?: string;
  recruiting?: boolean;
  page?: number;
  pageSize?: number;
  /** Whitelisted: sid | name | members | archetype | lang | recruiting */
  sortBy?: string;
  sortDir?: "asc" | "desc";
  [k: string]: string | number | boolean | undefined | null;
}
export const listOrgs = (q: OrgListQuery = {}, ctx: Ctx = {}) =>
  apiGet<PaginatedResponse<OrganizationDto>>("/api/organizations", q, ctx);

export const getOrg = (sid: string, ctx: Ctx = {}) =>
  apiGet<OrganizationDto>(
    `/api/organizations/${encodeURIComponent(sid)}`,
    undefined,
    ctx,
  );

/** One page of an org's members (their latest row), by handle. */
export const getOrgMembersPage = (
  sid: string,
  opts: { status: "active" | "former" | "all"; page: number; pageSize: number },
  ctx: Ctx = {},
) =>
  apiGet<PaginatedResponse<OrganizationMemberDto>>(
    `/api/organizations/${encodeURIComponent(sid)}/members`,
    opts,
    ctx,
  );

export const getOrgMemberChanges = (
  sid: string,
  limit = 50,
  ctx: Ctx = {},
) =>
  apiGet<ChangeEventDto[]>(
    `/api/organizations/${encodeURIComponent(sid)}/members/changes`,
    { limit },
    ctx,
  );

export const getOrgGrowth = (sid: string, ctx: Ctx = {}) =>
  apiGet<GrowthDataPoint[]>(
    `/api/organizations/${encodeURIComponent(sid)}/growth`,
    undefined,
    ctx,
  );

// ── Users ───────────────────────────────────────────────────
export interface UserListQuery {
  search?: string;
  page?: number;
  pageSize?: number;
  [k: string]: string | number | boolean | undefined | null;
}
export const listUsers = (q: UserListQuery = {}, ctx: Ctx = {}) =>
  apiGet<PaginatedResponse<UserProfileDto>>("/api/users", q, ctx);

export const getUser = (handle: string, ctx: Ctx = {}) =>
  apiGet<UserProfileDto>(
    `/api/users/${encodeURIComponent(handle)}`,
    undefined,
    ctx,
  );

export const getUserOrgs = (
  handle: string,
  include_inactive = false,
  ctx: Ctx = {},
) =>
  apiGet<OrganizationMemberDto[]>(
    `/api/users/${encodeURIComponent(handle)}/organizations`,
    { include_inactive },
    ctx,
  );

export const getUserHandleHistory = (handle: string, ctx: Ctx = {}) =>
  apiGet<UserHandleHistoryDto[]>(
    `/api/users/${encodeURIComponent(handle)}/history`,
    undefined,
    ctx,
  );

export const getUserChanges = (handle: string, limit = 50, ctx: Ctx = {}) =>
  apiGet<ChangeEventDto[]>(
    `/api/users/${encodeURIComponent(handle)}/changes`,
    { limit },
    ctx,
  );

// ── Changes ─────────────────────────────────────────────────
export interface ChangesQuery {
  changeType?: string;
  orgSid?: string;
  userHandle?: string;
  limit?: number;
  [k: string]: string | number | boolean | undefined | null;
}
export const listChanges = (q: ChangesQuery = {}, ctx: Ctx = {}) =>
  apiGet<ChangeEventDto[]>("/api/changes", q, ctx);

export const getChangesSummary = (days = 30, ctx: Ctx = {}) =>
  apiGet<ChangeSummaryDto[]>("/api/changes/summary", { days }, ctx);

// ── Auth ────────────────────────────────────────────────────
export const login = (body: LoginRequest) =>
  apiPost<AuthResponse>("/api/auth/login", body);
export const refresh = (refreshToken: string) =>
  apiPost<AuthResponse>("/api/auth/refresh", { refreshToken });
export const logout = (refreshToken: string) =>
  apiPost<{ message: string }>("/api/auth/logout", { refreshToken });
export const me = (bearerToken: string) =>
  apiGet<UserDto>("/api/auth/me", undefined, { bearerToken });

// ── API keys ────────────────────────────────────────────────
/** The signed-in user's keys, newest first (revoked ones included). */
export const listApiKeys = (ctx: Ctx = {}) =>
  apiGet<ApiKeyDto[]>("/api/api-keys", undefined, ctx);

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
