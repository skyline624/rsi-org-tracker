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
