import type { ChunkLike } from "./chunkTracker";
import type { SearchMember } from "./searchCursor";

export class DiscordProtocolError extends Error {
  constructor(message = "Le format de réponse Discord a changé : rien n'a été envoyé.") {
    super(message);
    this.name = "DiscordProtocolError";
  }
}

export type RestReply = { status: number; body: unknown; retryAfter?: number };
export const snowflake = (v: unknown): v is string => typeof v === "string" && /^[0-9]{17,20}$/.test(v);
export const record = (v: unknown): Record<string, unknown> | null =>
  typeof v === "object" && v !== null && !Array.isArray(v) ? v as Record<string, unknown> : null;

/** RestAPI resolves success and rejects errors using the same response wrapper. */
export function restReply(value: unknown): RestReply {
  const r = record(value);
  const status = r?.status;
  if (typeof status !== "number" || !Number.isInteger(status) || status < 100 || status > 599 || !r || !("body" in r)) {
    throw new DiscordProtocolError();
  }
  const body = record(r.body);
  const retry = body?.retry_after;
  return { status, body: r.body, retryAfter: typeof retry === "number" && Number.isFinite(retry) && retry >= 0 ? retry : undefined };
}

/** Accept only the member fields that will be sent, without borrowing fields from a cache. */
export function freshMember(value: unknown): SearchMember["member"] | null {
  const m = record(value);
  const user = record(m?.user);
  if (!m || !user || !snowflake(user.id) || typeof user.username !== "string" || [...user.username].length > 32
    || !Array.isArray(m.roles) || m.roles.length > 250 || !m.roles.every(snowflake)
    || !(m.joined_at === undefined || m.joined_at === null || (typeof m.joined_at === "string" && Number.isFinite(Date.parse(m.joined_at))))) return null;
  for (const name of [m.nick, user.global_name]) {
    if (name !== undefined && name !== null && (typeof name !== "string" || [...name].length > 32)) return null;
  }
  return {
    user: { id: user.id, username: user.username, global_name: user.global_name as string | null | undefined, bot: user.bot === true },
    roles: [...m.roles], nick: m.nick as string | null | undefined, joined_at: m.joined_at as string,
  };
}

export function searchPage(body: unknown): { members: SearchMember[]; expected: number } {
  const b = record(body);
  if (!b || !Array.isArray(b.members) || b.members.length > 1000
    || typeof b.total_result_count !== "number" || !Number.isSafeInteger(b.total_result_count)
    || b.total_result_count < 0 || b.total_result_count > 1_000_000) throw new DiscordProtocolError();
  const members = b.members.map(hit => {
    const member = freshMember(record(hit)?.member);
    if (!member) throw new DiscordProtocolError();
    return { member };
  });
  return { members, expected: b.total_result_count };
}

export function roleCounts(body: unknown): Record<string, number> {
  const b = record(body);
  if (!b) throw new DiscordProtocolError();
  const counts: Record<string, number> = {};
  for (const [id, count] of Object.entries(b)) {
    if (!snowflake(id) || typeof count !== "number" || !Number.isSafeInteger(count) || count < 0 || count > 1_000_000) throw new DiscordProtocolError();
    counts[id] = count;
  }
  return counts;
}

export function roleIds(body: unknown): string[] {
  const ids = Array.isArray(body) ? body : record(body)?.member_ids;
  if (!Array.isArray(ids) || ids.length > 100 || !ids.every(snowflake)) throw new DiscordProtocolError();
  return [...new Set(ids)];
}

export type FreshChunk = ChunkLike & { members: SearchMember["member"][] };

/** Strictly read the known raw/camelCase envelope variants; never read GuildMemberStore data. */
export function chunksFromAction(action: unknown): FreshChunk[] {
  const a = record(action);
  if (!a) return [];
  const values = Array.isArray(a.chunks) ? a.chunks : [a];
  if (values.length > 1000) return [];
  const chunks: FreshChunk[] = [];
  for (const value of values) {
    const c = record(value);
    const guildId = c?.guildId ?? c?.guild_id;
    const notFound = c?.notFound ?? c?.not_found;
    if (!c || !snowflake(guildId) || !Array.isArray(c.members) || c.members.length > 1000
      || (notFound !== undefined && (!Array.isArray(notFound) || !notFound.every(snowflake)))
      || (c.nonce !== undefined && typeof c.nonce !== "string")) continue;
    const members = c.members.map(freshMember);
    if (members.some(m => m === null)) continue;
    chunks.push({ guildId, members: members as SearchMember["member"][], notFound: notFound as string[] | undefined, nonce: c.nonce as string | undefined });
  }
  return chunks;
}
