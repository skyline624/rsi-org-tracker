/**
 * Display helpers for the Discord roster pages. Names come from Discord users (server,
 * role, account and nick names): pages render them as React text only, through
 * components/discord/DiscordText, which also cuts long ones with CSS. Nothing here
 * shortens a name.
 */

import type {
  DiscordDiscrepancyKind,
  DiscordReconciliation,
  DiscordSuggestionConfidence,
} from "@/lib/api/types";
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

/** Badges of an upload: complete or partial, then the mass-departure signal and the baseline. */
export function syncBadges(sync: {
  isComplete: boolean;
  massDepartureDetected: boolean;
  isBaseline?: boolean;
}): BadgeSpec[] {
  const badges: BadgeSpec[] = [
    sync.isComplete ? { label: "COMPLET", tone: "green" } : { label: "PARTIEL", tone: "orange" },
  ];
  if (sync.massDepartureDetected) badges.push({ label: "DÉPARTS MASSIFS", tone: "orange" });
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
