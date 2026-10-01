import { GuildMemberCountStore, GuildMemberStore, GuildRoleStore, GuildStore, PermissionStore, UserStore } from "@webpack/common";

import { SearchUnavailableError } from "../lib/collection";
import { DiscordProtocolError, snowflake } from "../lib/discordProtocol";
import { toSyncRole } from "../lib/payload";
import { memberSearch } from "./memberSearch";
import { refresh } from "./refresh";
import { roleMembers } from "./roleMembers";
import { runtimeDeps } from "./runtime";

/** Select per guild; cached members provide candidate IDs, never a member payload. */
export async function collectGuild(guildId: string, signal: AbortSignal, progress: Parameters<typeof runtimeDeps>[2]) {
  const started = performance.now();
  const collectedAt = new Date().toISOString();
  const guild = GuildStore?.getGuild(guildId);
  if (!guild || !snowflake(guild.id) || typeof guild.name !== "string") throw new Error("Ce serveur Discord n'est plus disponible.");
  if (typeof GuildRoleStore?.getSortedRoles !== "function" || typeof GuildMemberStore?.getMemberIds !== "function") throw new DiscordProtocolError();
  const d = runtimeDeps(guildId, signal, progress);
  let collection;
  if (typeof PermissionStore?.canAccessMemberSafetyPage === "function" && PermissionStore.canAccessMemberSafetyPage(guild)) {
    try { collection = await memberSearch(d); }
    catch (error) { if (!(error instanceof SearchUnavailableError)) throw error; }
  }
  if (!collection) {
    const cached = GuildMemberStore.getMemberIds(guildId);
    if (!Array.isArray(cached)) throw new DiscordProtocolError();
    const cachedIds = cached.filter(snowflake);
    const self = UserStore?.getCurrentUser()?.id;
    if (snowflake(self)) cachedIds.push(self);
    const roles = GuildRoleStore.getSortedRoles(guildId).map(r => ({ id: r.id, managed: r.managed === true }));
    const candidates = await roleMembers(d, roles, [...new Set(cachedIds)]);
    collection = await refresh(d, candidates.ids, candidates.method);
    if (candidates.stopReason && collection.stopReason === "exhausted") collection.stopReason = candidates.stopReason;
  }
  d.pacer.checkCancellation();
  const roles = GuildRoleStore.getSortedRoles(guildId).filter(role => role.id !== guildId).map(toSyncRole);
  let count: number | null = null;
  if (typeof GuildMemberCountStore?.getMemberCount === "function") {
    const value = GuildMemberCountStore.getMemberCount(guildId);
    if (typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= 1_000_000) count = value;
  }
  return {
    ...collection, roles, durationMs: performance.now() - started, collectedAt,
    guild: { id: guild.id, name: guild.name, icon: guild.icon ?? null, memberCount: count },
  };
}
