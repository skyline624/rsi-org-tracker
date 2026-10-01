import { Constants, FluxDispatcher, RestAPI } from "@webpack/common";

import type { CollectionDeps } from "../lib/collection";
import { DiscordProtocolError, restReply } from "../lib/discordProtocol";
import { createPacer, DEFAULT_MAX_DURATION_MS } from "../lib/pacing";
import type { SyncMethod } from "../lib/payload";

/** Guard all undocumented endpoints and reply wrappers at runtime, not only in TypeScript. */
export function runtimeDeps(guildId: string, signal: AbortSignal, progress: (method: SyncMethod, count: number) => void): CollectionDeps {
  const now = () => performance.now();
  const deadline = now() + DEFAULT_MAX_DURATION_MS;
  return {
    guildId, signal, deadline, now, progress,
    pacer: createPacer({ signal, now }),
    nonce: () => crypto.randomUUID().replaceAll("-", ""),
    async rest(kind, body, roleId) {
      const name = kind === "search" ? "GUILD_MEMBER_SEARCH" : kind === "role-counts" ? "GUILD_ROLE_MEMBER_COUNTS" : "GUILD_ROLE_MEMBER_IDS";
      const endpoint = Constants?.Endpoints?.[name];
      if (typeof endpoint !== "function") throw new DiscordProtocolError(`Discord ne fournit plus ${name} : rien envoyé.`);
      const url = kind === "role-ids" ? endpoint(guildId, roleId) : endpoint(guildId);
      if (typeof url !== "string" || !url.startsWith(`/guilds/${guildId}/`)) throw new DiscordProtocolError();
      let response: unknown;
      try {
        response = kind === "search" ? await RestAPI.post({ url, body, retries: 0 }) : await RestAPI.get({ url, retries: 0 });
      } catch (error) { response = error; }
      return restReply(response);
    },
    subscribeChunks(listener) {
      if (typeof FluxDispatcher?.subscribe !== "function") throw new DiscordProtocolError();
      FluxDispatcher.subscribe("GUILD_MEMBERS_CHUNK_BATCH", listener);
      return () => FluxDispatcher.unsubscribe("GUILD_MEMBERS_CHUNK_BATCH", listener);
    },
    async dispatchMembers(userIds, nonce) {
      if (typeof FluxDispatcher?.dispatch !== "function") throw new DiscordProtocolError();
      await FluxDispatcher.dispatch({ type: "GUILD_MEMBERS_REQUEST", guildIds: [guildId], userIds, nonce });
    },
  };
}
