/**
 * Fonctions typées par endpoint. Importées depuis les pages RSC et les hooks
 * TanStack Query. Le serveur passe `{ serverSide: true }` ; le client rien.
 */

import { apiDelete, apiGet, apiPost, apiPut } from "./client";
import type {
  ApiKeyDto,
  ArchetypeStatsDto,
  AuthResponse,
  ChangeEventDto,
  ChangeSummaryDto,
  CreatedApiKeyDto,
  CycleStatusDto,
  GrowthDataPoint,
  LoginRequest,
  MemberActivityDto,
  OrganizationDto,
  OrganizationMemberDto,
  OrganizationTopDto,
  PaginatedResponse,
  RegisterRequest,
  StatsOverviewDto,
  TimelinePointDto,
  UserDto,
  UserHandleHistoryDto,
  UserProfileDto,
  UserResolveDto,
} from "./types";

type Ctx = { serverSide?: boolean; bearerToken?: string };

// ── Health ──────────────────────────────────────────────────
export const getHealth = (ctx: Ctx = {}) =>
  apiGet<{ status: string }>("/api/health", undefined, ctx);
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

export const getOrgMembers = (
  sid: string,
  opts: { at_time?: string; include_inactive?: boolean } = {},
  ctx: Ctx = {},
) =>
  apiGet<OrganizationMemberDto[]>(
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

export const getUserByCitizenId = (id: number, ctx: Ctx = {}) =>
  apiGet<UserProfileDto>(`/api/users/by-citizen-id/${id}`, undefined, ctx);

export const resolveUser = (handle: string, ctx: Ctx = {}) =>
  apiGet<UserResolveDto>(
    `/api/users/resolve/${encodeURIComponent(handle)}`,
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
export const register = (body: RegisterRequest) =>
  apiPost<UserDto>("/api/auth/register", body);
export const login = (body: LoginRequest) =>
  apiPost<AuthResponse>("/api/auth/login", body);
export const refresh = (refreshToken: string) =>
  apiPost<AuthResponse>("/api/auth/refresh", { refreshToken });
export const logout = (refreshToken: string) =>
  apiPost<{ message: string }>("/api/auth/logout", { refreshToken });
export const me = (bearerToken: string) =>
  apiGet<UserDto>("/api/auth/me", undefined, { bearerToken });
export const forgotPassword = (email: string) =>
  apiPost<{ message: string }>("/api/auth/forgot-password", { email });
export const resetPassword = (token: string, newPassword: string) =>
  apiPost<{ message: string }>("/api/auth/reset-password", {
    token,
    newPassword,
  });

// ── ApiKeys ─────────────────────────────────────────────────
export const listApiKeys = (bearerToken: string) =>
  apiGet<ApiKeyDto[]>("/api/api-keys", undefined, { bearerToken });
export const createApiKey = (
  body: { name: string; expiresAt?: string },
  bearerToken: string,
) => apiPost<CreatedApiKeyDto>("/api/api-keys", body, { bearerToken });
export const revokeApiKey = (id: number, bearerToken: string) =>
  apiDelete<void>(`/api/api-keys/${id}`, { bearerToken });
