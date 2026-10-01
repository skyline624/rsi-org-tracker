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
  /**
   * False for roster-only members surfaced from organization_members that never
   * got a CitizenId (citizenId is then 0). True for enriched `users` rows.
   */
  isEnriched: boolean;
}

export interface UserHandleHistoryDto {
  userHandle: string;
  firstSeen: string;
  lastSeen: string;
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
  /** Detail only: last read of the org page (Phase 2), changed or not. */
  contentCheckedAt?: string | null;
  /** Detail only: last read of the roster (Phase 3). */
  membersCollectedAt?: string | null;
}

export interface OrganizationMemberDto {
  orgSid?: string;
  orgName?: string | null;
  userHandle: string;
  citizenId?: number | null;
  displayName?: string | null;
  rank?: string | null;
  roles?: string[] | null;
  urlImage?: string | null;
  /** When the member was last seen in this org. */
  timestamp: string;
  /** First snapshot in which the member appeared in this org ("member since"). */
  memberSince?: string | null;
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

// ── API keys ────────────────────────────────────────────────
export interface ApiKeyDto {
  id: number;
  name: string;
  keyPrefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
  isRevoked: boolean;
  /** null: full access; "discord:ingest": Discord roster uploads only. */
  scope: string | null;
}

/** Returned once, at creation: the raw key is never shown again. */
export type CreatedApiKeyDto = ApiKeyDto & { rawKey: string };

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
  massDepartureDetected: boolean;
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
  massDepartureDetected: boolean;
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
