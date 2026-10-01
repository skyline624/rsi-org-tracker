import { createChunkTracker } from "./chunkTracker";
import { collectionSleep, guarded } from "./collectionAsync";
import type { CollectionStopReason } from "./coverage";
import { chunksFromAction, DiscordProtocolError, record, type RestReply, roleCounts, roleIds, searchPage } from "./discordProtocol";
import { AbortedError, CapReachedError, DurationReachedError, type Pacer } from "./pacing";
import { MAX_MEMBERS, type SyncMember, type SyncMethod } from "./payload";
import { mergePage, nextAfter, toSyncMember } from "./searchCursor";

export type CollectionResult = { method: SyncMethod; members: SyncMember[]; expected: number | null; stopReason: CollectionStopReason };
export type CollectionDeps = {
  guildId: string;
  signal: AbortSignal;
  pacer: Pacer;
  deadline: number;
  now(): number;
  rest(kind: "search" | "role-counts" | "role-ids", body?: Record<string, unknown>, roleId?: string): Promise<RestReply>;
  subscribeChunks(listener: (action: unknown) => void): () => void;
  dispatchMembers(ids: string[], nonce: string): Promise<void>;
  nonce(): string;
  progress(method: SyncMethod, count: number): void;
};

export class SearchUnavailableError extends Error {}

function reason(error: unknown): CollectionStopReason | null {
  if (error instanceof AbortedError) throw error;
  if (error instanceof CapReachedError) return "call_cap";
  if (error instanceof DurationReachedError) return "deadline";
  return null;
}

/** Every actual REST attempt, including retries, consumes the same guild call budget. */
async function request(d: CollectionDeps, kind: "search" | "role-counts" | "role-ids", body?: Record<string, unknown>, roleId?: string, reserve = 0): Promise<RestReply> {
  for (;;) {
    d.pacer.check();
    if (d.pacer.callsRemaining <= reserve) throw new CapReachedError();
    await d.pacer.beforeRest();
    const reply = await guarded(d.rest(kind, body, roleId), d.signal, d.deadline, d.now);
    d.pacer.check();
    if (reply.status !== 429) return reply;
    await collectionSleep((reply.retryAfter ?? 30) * 1000, d.signal, d.deadline, d.now);
  }
}

/** Paginated member search; abnormal termination always preserves an explicitly partial result. */
export async function collectSearch(d: CollectionDeps): Promise<CollectionResult> {
  const seen = new Map<string, SyncMember>();
  let expected: number | null = null;
  let after: ReturnType<typeof nextAfter> = null;
  let indexingAttempts = 0;
  const result = (stopReason: CollectionStopReason): CollectionResult => ({ method: "member-search", members: [...seen.values()], expected, stopReason });
  try {
    for (;;) {
      const reply = await request(d, "search", { limit: 1000, sort: 2, ...(after ? { after } : {}) });
      if (reply.status === 403) throw new SearchUnavailableError();
      if (reply.status === 202 && record(reply.body)?.code === 110000) {
        if (++indexingAttempts >= 3) throw new SearchUnavailableError();
        await collectionSleep(Math.max(reply.retryAfter ?? 5, 5) * 1000, d.signal, d.deadline, d.now);
        continue;
      }
      if (reply.status !== 200) return result("error");
      indexingAttempts = 0;
      const page = searchPage(reply.body);
      expected = page.expected;
      const { added } = mergePage(seen, page.members);
      d.progress("member-search", seen.size);
      if (seen.size > MAX_MEMBERS) throw new Error("Serveur trop grand pour un envoi (plus de 50 000 membres)");
      if (page.members.length === 0) return result("exhausted");
      if (added === 0) return result("cursor_stalled");
      if (page.members.length < 1000) return result("exhausted");
      const next = nextAfter(page.members);
      if (!next) return result("invalid_cursor");
      if (after && next.guild_joined_at === after.guild_joined_at && next.user_id === after.user_id) return result("cursor_stalled");
      after = next;
    }
  } catch (error) {
    const stop = reason(error);
    if (stop) return result(stop);
    if (!(error instanceof DiscordProtocolError) && !(error instanceof SearchUnavailableError)) return result("error");
    throw error;
  }
}

/** Candidate IDs only; every candidate must still be refreshed through a correlated chunk. */
export async function collectRoleCandidates(d: CollectionDeps, roles: { id: string; managed: boolean }[], cachedIds: string[]): Promise<{ ids: string[]; method: "role-members" | "cache"; stopReason?: CollectionStopReason }> {
  const candidates = new Set(cachedIds);
  let method: "role-members" | "cache" = "cache";
  const reserve = () => Math.max(1, Math.ceil(candidates.size / 100));
  const result = (stopReason?: CollectionStopReason) => ({ ids: [...candidates], method, ...(stopReason ? { stopReason } : {}) });
  try {
    const reply = await request(d, "role-counts", undefined, undefined, reserve());
    if (reply.status !== 200) return result("error");
    const counts = roleCounts(reply.body);
    method = "role-members";
    for (const role of roles) {
      if (role.id === d.guildId || role.managed || !counts[role.id]) continue;
      const members = await request(d, "role-ids", undefined, role.id, reserve());
      if (members.status !== 200) return result("error");
      for (const id of roleIds(members.body)) candidates.add(id);
    }
    return result();
  } catch (error) {
    const stop = reason(error);
    return result(stop ?? "error");
  }
}

/** Refresh only requested IDs from nonce-matching chunks. Missing runtime correlation fails closed. */
export async function collectRefresh(d: CollectionDeps, userIds: string[], method: "role-members" | "cache", timeoutMs = 10_000): Promise<CollectionResult> {
  const ids = [...new Set(userIds)];
  const seen = new Map<string, SyncMember>();
  let anyMatched = false;
  const result = (stopReason: CollectionStopReason): CollectionResult => ({ method, members: [...seen.values()], expected: null, stopReason });
  try {
    for (let offset = 0; offset < ids.length; offset += 100) {
      await d.pacer.beforeGateway();
      const batch = ids.slice(offset, offset + 100);
      const nonce = d.nonce();
      if (!nonce || new TextEncoder().encode(nonce).length > 32) throw new DiscordProtocolError("Impossible de corréler la requête Discord : nonce invalide.");
      const tracker = createChunkTracker(d.guildId, batch, nonce);
      let matched = false;
      let resolveBatch!: () => void;
      const received = new Promise<void>(resolve => { resolveBatch = resolve; });
      const unsubscribe = d.subscribeChunks(action => {
        for (const chunk of chunksFromAction(action)) {
          if (chunk.guildId !== d.guildId || chunk.nonce !== nonce) continue;
          matched = true;
          anyMatched = true;
          const before = new Set(tracker.present);
          tracker.accept(chunk);
          for (const m of chunk.members) {
            if (tracker.present.has(m.user.id) && !before.has(m.user.id)) {
              seen.set(m.user.id, toSyncMember({ member: m }));
              before.add(m.user.id);
            }
          }
          if (tracker.done) resolveBatch();
        }
      });
      let timer: ReturnType<typeof setTimeout> | undefined;
      let timedOut = false;
      try {
        await guarded(d.dispatchMembers(batch, nonce), d.signal, d.deadline, d.now);
        d.pacer.check();
        const timeout = new Promise<void>(resolve => {
          timer = setTimeout(() => { timedOut = true; resolve(); }, timeoutMs);
        });
        await guarded(Promise.race([received, timeout]), d.signal, d.deadline, d.now);
        d.pacer.check();
      } finally {
        clearTimeout(timer);
        unsubscribe();
      }
      if (!matched && !anyMatched) throw new DiscordProtocolError("Discord n'a pas renvoyé de chunk corrélé au nonce de collecte. Rien n'a été envoyé.");
      d.progress(method, seen.size);
      if (timedOut && offset + 100 < ids.length) await d.pacer.afterGatewayTimeout();
    }
    return result("exhausted");
  } catch (error) {
    const stop = reason(error);
    if (stop) return result(stop);
    if (anyMatched && !(error instanceof DiscordProtocolError)) return result("error");
    throw error;
  }
}
