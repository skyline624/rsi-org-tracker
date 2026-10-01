/** Same bound as the API's [RequestSizeLimit] and nginx's client_max_body_size 25m. */
export const MAX_BODY_BYTES = 25 * 1024 * 1024;
/** Same bound as the API's validator (members ≤ 50 000). */
export const MAX_MEMBERS = 50_000;

export type SyncMethod = "member-search" | "role-members" | "cache";

export type SyncRole = { id: string; name: string; position: number; color: string | null; hoist: boolean; managed: boolean };

export type SyncMember = {
  userId: string;
  username: string;
  globalName: string | null;
  nick: string | null;
  roleIds: string[];
  joinedAt: string | null;
  bot: boolean;
};

/** The JSON body of POST /ingest/discord/guilds/{guildId}/syncs (the API's DiscordSyncRequest). */
export type SyncPayload = {
  pluginVersion: string;
  collectedAt: string;
  collectionDurationMs: number;
  guild: { id: string; name: string; icon: string | null; memberCount: number | null };
  coverage: { method: SyncMethod; complete: boolean; expectedCount: number | null; collectedCount: number };
  roles: SyncRole[];
  members: SyncMember[];
};

/**
 * The fields of a Discord role the tracker keeps. A client `Role` from GuildRoleStore fits
 * this type as is, bigint `permissions` included: toSyncRole never copies it.
 */
export type RoleLike = {
  id: string;
  name: string;
  position: number;
  color?: number | null;
  colorString?: string | null;
  hoist?: boolean;
  managed?: boolean;
};

const HEX_COLOR = /^#[0-9a-f]{6}$/i;

/** Maps a client role to the payload's role: "#rrggbb" in lower case, or null for "no colour" (0). */
export function toSyncRole(role: RoleLike): SyncRole {
  let color: string | null = null;
  if (typeof role.colorString === "string" && HEX_COLOR.test(role.colorString)) {
    color = role.colorString.toLowerCase();
  } else if (typeof role.color === "number" && Number.isInteger(role.color) && role.color > 0 && role.color <= 0xffffff) {
    color = `#${role.color.toString(16).padStart(6, "0")}`;
  }
  return {
    id: String(role.id),
    name: role.name,
    position: role.position,
    color,
    hoist: role.hoist === true,
    managed: role.managed === true,
  };
}

/**
 * Number of bytes of `text` in UTF-8, as Buffer.byteLength counts them (a lone surrogate is
 * replaced by U+FFFD, 3 bytes). The renderer has no Buffer, so it is counted here without
 * allocating a copy of a body that may weigh 25 MiB.
 */
export function utf8ByteLength(text: string): number {
  let bytes = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code < 0x80) bytes += 1;
    else if (code < 0x800) bytes += 2;
    else if (code >= 0xd800 && code <= 0xdbff && i + 1 < text.length
      && text.charCodeAt(i + 1) >= 0xdc00 && text.charCodeAt(i + 1) <= 0xdfff) {
      bytes += 4;
      i++;
    } else bytes += 3;
  }
  return bytes;
}

/**
 * Builds the JSON body, copying each field of the contract by name: whatever else the caller's
 * objects carry (client fields, bigint permissions that JSON.stringify refuses) never reaches
 * the tracker. Refuses more than MAX_MEMBERS members or more than MAX_BODY_BYTES bytes, the
 * bounds the tracker would answer with a 400 or a 413 after a long upload.
 */
export function buildPayload(p: SyncPayload):
  { ok: true; json: string; bytes: number } | { ok: false; reason: "too_many_members" | "too_large" } {
  if (p.members.length > MAX_MEMBERS) return { ok: false, reason: "too_many_members" };

  const body: SyncPayload = {
    pluginVersion: p.pluginVersion,
    collectedAt: p.collectedAt,
    collectionDurationMs: Math.max(0, Math.round(p.collectionDurationMs)),
    guild: {
      id: String(p.guild.id),
      name: p.guild.name,
      icon: p.guild.icon ?? null,
      memberCount: p.guild.memberCount ?? null,
    },
    coverage: {
      method: p.coverage.method,
      complete: p.coverage.complete === true,
      expectedCount: p.coverage.expectedCount ?? null,
      collectedCount: p.coverage.collectedCount,
    },
    roles: p.roles.map(r => ({
      id: String(r.id),
      name: r.name,
      position: r.position,
      color: r.color ?? null,
      hoist: r.hoist === true,
      managed: r.managed === true,
    })),
    members: p.members.map(m => ({
      userId: String(m.userId),
      username: m.username,
      globalName: m.globalName ?? null,
      nick: m.nick ?? null,
      roleIds: m.roleIds.map(String),
      joinedAt: m.joinedAt ?? null,
      bot: m.bot === true,
    })),
  };

  const json = JSON.stringify(body);
  const bytes = utf8ByteLength(json);
  if (bytes > MAX_BODY_BYTES) return { ok: false, reason: "too_large" };
  return { ok: true, json, bytes };
}
