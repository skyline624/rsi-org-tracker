/** One chunk of a GUILD_MEMBERS_CHUNK_BATCH Flux action, reduced to what the tracker reads. */
export type ChunkLike = { guildId: string; members: { user: { id: string } }[]; notFound?: string[]; nonce?: string };

/**
 * Follows one GUILD_MEMBERS_REQUEST batch: each requested id is resolved once, as present (it
 * came back in `members`, with fresh data) or as not found (it left the guild). Chunks of another
 * guild and ids that were not requested are ignored. When a nonce is supplied, missing or
 * different response nonces are also ignored. The Discord adapter must first verify that its
 * request nonce reaches the gateway and comes back in Flux; this module does not establish that.
 * Without a nonce, overlapping requests cannot be distinguished. GuildMemberStore does not count.
 */
export function createChunkTracker(guildId: string, userIds: string[], nonce?: string): {
  accept(chunk: ChunkLike): void;
  readonly done: boolean;
  readonly present: Set<string>;
  readonly notFound: Set<string>;
  unresolved(): string[];
} {
  const requested = [...new Set(userIds)];
  const wanted = new Set(requested);
  const present = new Set<string>();
  const notFound = new Set<string>();
  const isOpen = (id: unknown): id is string =>
    typeof id === "string" && wanted.has(id) && !present.has(id) && !notFound.has(id);

  return {
    accept(chunk: ChunkLike) {
      if (chunk?.guildId !== guildId) return;
      if (nonce !== undefined && chunk.nonce !== nonce) return;
      for (const member of Array.isArray(chunk.members) ? chunk.members : []) {
        const id = member?.user?.id;
        if (isOpen(id)) present.add(id);
      }
      for (const id of Array.isArray(chunk.notFound) ? chunk.notFound : []) {
        if (isOpen(id)) notFound.add(id);
      }
    },
    get done() {
      return present.size + notFound.size === wanted.size;
    },
    present,
    notFound,
    unresolved() {
      return requested.filter(id => !present.has(id) && !notFound.has(id));
    },
  };
}
