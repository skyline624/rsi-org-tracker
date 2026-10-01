import type { SyncMember } from "./payload";

/** One entry of `members` in Discord's member search answer (POST guilds/{id}/members-search). */
export type SearchMember = {
  member: {
    user: { id: string; username: string; global_name?: string | null; bot?: boolean };
    nick?: string | null;
    roles: string[];
    joined_at: string;
  };
};

/**
 * The `after` cursor for the next page of a JOINED_AT_ASC search (sort: 2): the LAST member's
 * joined_at as integer milliseconds and its user id. Null for an empty page, or when that date
 * cannot be read: the caller then stops, and the sync stays partial, rather than restarting
 * from the first page.
 */
export function nextAfter(page: SearchMember[]): { guild_joined_at: number; user_id: string } | null {
  const last = page[page.length - 1];
  if (last === undefined) return null;
  const joinedAt = Date.parse(last.member.joined_at);
  if (Number.isNaN(joinedAt)) return null;
  return { guild_joined_at: Math.trunc(joinedAt), user_id: last.member.user.id };
}

/**
 * Adds the page's members not seen yet (by user id) and says how many were new. A page that
 * adds none means the cursor no longer moves: the caller stops and sends a partial sync.
 */
export function mergePage(seen: Map<string, SyncMember>, page: SearchMember[]): { added: number } {
  let added = 0;
  for (const hit of page) {
    const id = hit.member.user.id;
    const previous = seen.get(id);
    if (previous) {
      if (Date.parse(hit.member.joined_at) > Date.parse(previous.joinedAt ?? "")) seen.set(id, toSyncMember(hit));
      continue;
    }
    seen.set(id, toSyncMember(hit));
    added++;
  }
  return { added };
}

/** Maps a search hit to the payload's member, reading only what the tracker stores. */
export function toSyncMember(m: SearchMember): SyncMember {
  const { user } = m.member;
  return {
    userId: user.id,
    username: user.username,
    globalName: user.global_name ?? null,
    nick: m.member.nick ?? null,
    roleIds: [...m.member.roles],
    joinedAt: m.member.joined_at ?? null,
    bot: user.bot === true,
  };
}
