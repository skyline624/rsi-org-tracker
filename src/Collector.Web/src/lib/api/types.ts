/**
 * Types miroirs des DTOs C# de `Collector.Api/Dtos/**`.
 * Toute divergence doit être alignée manuellement (ou auto-générée v2 via swagger.json).
 */

// ── Common ──────────────────────────────────────────────────
export interface PaginatedResponse<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

// ── Auth ────────────────────────────────────────────────────
export interface UserDto {
  id: number;
  username: string;
  email: string;
  isAdmin: boolean;
  createdAt: string;
  lastLoginAt?: string | null;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: UserDto;
}

export interface RegisterRequest {
  username: string;
  email: string;
  password: string;
}

export interface LoginRequest {
  username: string;
  password: string;
}

// ── Users ───────────────────────────────────────────────────
export interface UserProfileDto {
  citizenId: number;
  userHandle: string;
  displayName?: string | null;
  urlImage?: string | null;
  bio?: string | null;
  location?: string | null;
  enlisted?: string | null;
  updatedAt: string;
}

export interface UserHandleHistoryDto {
  userHandle: string;
  firstSeen: string;
  lastSeen: string;
}

export interface UserResolveDto {
  citizenId: number;
  currentHandle: string;
  requestedHandle?: string;
  handleChanged: boolean;
}

// ── Organizations ───────────────────────────────────────────
export interface OrganizationDto {
  sid: string;
  name: string;
  urlImage?: string | null;
  urlCorpo?: string | null;
  archetype?: string | null;
  lang?: string | null;
  commitment?: string | null;
  recruiting?: boolean | null;
  roleplay?: boolean | null;
  membersCount: number;
  timestamp: string;
  description?: string | null;
  focusPrimaryName?: string | null;
  focusSecondaryName?: string | null;
}

export interface OrganizationMemberDto {
  orgSid?: string;
  userHandle: string;
  citizenId?: number | null;
  displayName?: string | null;
  rank?: string | null;
  urlImage?: string | null;
  timestamp: string;
  isActive: boolean;
}

export interface GrowthDataPoint {
  date: string; // yyyy-MM-dd
  membersCount: number;
  delta: number;
}

// ── Changes ─────────────────────────────────────────────────
export interface ChangeEventDto {
  id: number;
  timestamp: string;
  entityType: string;
  entityId: string;
  changeType: string;
  oldValue?: string | null;
  newValue?: string | null;
  orgSid?: string | null;
  userHandle?: string | null;
}

export interface ChangeSummaryDto {
  changeType: string;
  count: number;
}

// ── Stats ───────────────────────────────────────────────────
export interface StatsOverviewDto {
  totalOrganizations: number;
  totalUsers: number;
  lastCollectionAt?: string | null;
}

export interface TimelinePointDto {
  date: string;
  changeCount: number;
}

export interface ArchetypeStatsDto {
  archetype: string;
  count: number;
}

export interface MemberActivityDto {
  date: string;
  joins: number;
  leaves: number;
  total: number;
}

export interface OrganizationTopDto {
  sid: string;
  name: string;
  membersCount: number;
  archetype?: string | null;
}

// ── Health ──────────────────────────────────────────────────
export interface CycleStatusDto {
  queue_pending: number;
  queue_stuck: number;
  last_member_collection: { org_sid: string; at: string } | null;
  discovered_orgs: number;
}

// ── ApiKeys ─────────────────────────────────────────────────
export interface ApiKeyDto {
  id: number;
  name: string;
  keyPrefix: string;
  createdAt: string;
  lastUsedAt?: string | null;
  expiresAt?: string | null;
  isRevoked: boolean;
}

export interface CreatedApiKeyDto extends ApiKeyDto {
  rawKey: string;
}
