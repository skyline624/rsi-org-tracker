import { getEventListeners } from "node:events";
import { afterEach, describe, expect, it, vi } from "vitest";

import { collectRefresh, collectRoleCandidates, collectSearch, SearchUnavailableError, type CollectionDeps } from "../scTracker.desktop/lib/collection";
import { collectionSleep, guarded } from "../scTracker.desktop/lib/collectionAsync";
import { computeCoverage } from "../scTracker.desktop/lib/coverage";
import { DiscordProtocolError } from "../scTracker.desktop/lib/discordProtocol";
import { AbortedError, CapReachedError, createPacer, DurationReachedError } from "../scTracker.desktop/lib/pacing";

const guildId = "100000000000000001";
const id = (n: number) => String(200000000000000000n + BigInt(n));
const raw = (n: number, username = `user${n}`) => ({ user: { id: id(n), username }, roles: [], joined_at: "2026-09-30T00:00:00.000Z" });
const page = (ns: number[], expected: number) => ({ status: 200, body: { members: ns.map(n => ({ member: raw(n) })), total_result_count: expected } });
function deps() {
  const controller = new AbortController();
  let listener: (action: unknown) => void = () => {};
  const unsubscribe = vi.fn();
  const d: CollectionDeps = {
    guildId, signal: controller.signal, deadline: Infinity, now: Date.now,
    pacer: {
      callsUsed: 0, callsRemaining: 200,
      beforeRest: vi.fn(async () => {}), beforeGateway: vi.fn(async () => {}), afterGatewayTimeout: vi.fn(async () => {}),
      check: () => { if (controller.signal.aborted) throw new AbortedError(); },
      checkCancellation: () => { if (controller.signal.aborted) throw new AbortedError(); },
    },
    rest: vi.fn(async () => page([], 0)),
    subscribeChunks: fn => { listener = fn; return unsubscribe; },
    dispatchMembers: vi.fn(async () => {}), nonce: () => "batch-nonce", progress: vi.fn(),
  };
  return { d, controller, unsubscribe, emit: (action: unknown) => listener(action) };
}
afterEach(() => vi.useRealTimers());

describe("member search collector", () => {
  it("paginates with a millisecond cursor and sends only response data", async () => {
    const { d } = deps();
    const calls: unknown[] = [];
    d.rest = vi.fn(async (_, body) => { calls.push(body); return calls.length === 1 ? page(Array.from({ length: 1000 }, (_, n) => n + 1), 1001) : page([1001], 1001); });
    const result = await collectSearch(d);
    expect(result.members).toHaveLength(1001);
    expect(result.stopReason).toBe("exhausted");
    expect(computeCoverage(result.method, result.members.length, result.expected, result.stopReason).complete).toBe(true);
    expect(calls).toEqual([{ limit: 1000, sort: 2 }, { limit: 1000, sort: 2, after: { guild_joined_at: Date.parse(raw(1).joined_at), user_id: id(1000) } }]);
    expect(result.members[0]).toEqual({ userId: id(1), username: "user1", globalName: null, nick: null, roleIds: [], joinedAt: raw(1).joined_at, bot: false });
  });
  it("a repeated full page is partial even when expected equals collected", async () => {
    const { d } = deps();
    d.rest = vi.fn(async () => page(Array.from({ length: 1000 }, (_, n) => n + 1), 1000));
    const result = await collectSearch(d);
    expect(result.stopReason).toBe("cursor_stalled");
    expect(computeCoverage(result.method, result.members.length, result.expected, result.stopReason).complete).toBe(false);
    expect(d.rest).toHaveBeenCalledTimes(2);
  });
  it("keeps a full page with a missing cursor explicitly partial", async () => {
    const { d } = deps();
    d.rest = async () => ({ status: 200, body: { members: Array.from({ length: 1000 }, (_, n) => ({ member: { ...raw(n + 1), joined_at: null } })), total_result_count: 1000 } });
    expect((await collectSearch(d)).stopReason).toBe("invalid_cursor");
  });
  it.each([new CapReachedError(), new DurationReachedError()])("preserves collected members at the collection bound: %s", async error => {
    const { d } = deps();
    d.rest = vi.fn(async () => page(Array.from({ length: 1000 }, (_, n) => n + 1), 1000));
    d.pacer.beforeRest = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(error);
    const result = await collectSearch(d);
    expect(result.members).toHaveLength(1000);
    expect(result.stopReason).toBe(error instanceof CapReachedError ? "call_cap" : "deadline");
  });
  it("returns search permission failure for fallback", async () => {
    const { d } = deps(); d.rest = async () => ({ status: 403, body: {} });
    await expect(collectSearch(d)).rejects.toBeInstanceOf(SearchUnavailableError);
  });
  it("preserves a successful page after a REST transport error", async () => {
    const { d } = deps();
    d.rest = vi.fn().mockResolvedValueOnce(page(Array.from({ length: 1000 }, (_, n) => n + 1), 1001))
      .mockRejectedValueOnce(new Error("Disconnected"));
    const result = await collectSearch(d);
    expect(result.members).toHaveLength(1000);
    expect(result.stopReason).toBe("error");
  });
  it("resets index retries after a successful page and waits at least five seconds", async () => {
    vi.useFakeTimers(); const { d } = deps();
    const indexing = { status: 202, body: { code: 110000 }, retryAfter: 0 };
    d.rest = vi.fn().mockResolvedValueOnce(indexing).mockResolvedValueOnce(indexing)
      .mockResolvedValueOnce(page(Array.from({ length: 1000 }, (_, n) => n + 1), 1001))
      .mockResolvedValueOnce(indexing).mockResolvedValueOnce(page([1001], 1001));
    const promise = collectSearch(d);
    await vi.advanceTimersByTimeAsync(4999); expect(d.rest).toHaveBeenCalledTimes(1);
    await vi.runAllTimersAsync();
    expect((await promise).members).toHaveLength(1001);
    expect(d.rest).toHaveBeenCalledTimes(5);
  });
  it("makes at most three index attempts then falls back", async () => {
    vi.useFakeTimers(); const { d } = deps(); d.rest = vi.fn(async () => ({ status: 202, body: { code: 110000 }, retryAfter: 1 }));
    const promise = expect(collectSearch(d)).rejects.toBeInstanceOf(SearchUnavailableError);
    await vi.runAllTimersAsync(); await promise;
    expect(d.rest).toHaveBeenCalledTimes(3); expect(d.pacer.beforeRest).toHaveBeenCalledTimes(3);
  });
  it("pays for each 429 retry and respects retry_after", async () => {
    vi.useFakeTimers(); const { d } = deps();
    d.rest = vi.fn().mockResolvedValueOnce({ status: 429, body: {}, retryAfter: 2 }).mockResolvedValueOnce(page([1], 1));
    const promise = collectSearch(d);
    await vi.advanceTimersByTimeAsync(1999); expect(d.rest).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1); const result = await promise;
    expect(result.members).toHaveLength(1); expect(d.pacer.beforeRest).toHaveBeenCalledTimes(2);
  });
  it("aborts a non-cancellable REST promise and consumes its late rejection", async () => {
    const { d, controller } = deps(); let reject!: (error: Error) => void;
    d.rest = () => new Promise((_, r) => { reject = r; });
    const promise = expect(collectSearch(d)).rejects.toBeInstanceOf(AbortedError);
    await Promise.resolve(); await Promise.resolve(); controller.abort(); await promise;
    expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
    reject(new Error("late")); await Promise.resolve();
  });
  it("refuses an unknown search response instead of forwarding cache fields", async () => {
    const { d } = deps(); d.rest = async () => ({ status: 200, body: { members: [{ user: raw(1).user }], total_result_count: 1 } });
    await expect(collectSearch(d)).rejects.toBeInstanceOf(DiscordProtocolError);
  });
});

describe("role candidate collector", () => {
  it("skips everyone, managed and empty roles, and includes cached IDs", async () => {
    const { d } = deps(); const roleId = id(30);
    d.rest = vi.fn(async kind => kind === "role-counts" ? { status: 200, body: { [guildId]: 20, [roleId]: 1, [id(31)]: 2, [id(32)]: 0 } } : { status: 200, body: { member_ids: [id(1)] } });
    expect(await collectRoleCandidates(d, [{ id: guildId, managed: false }, { id: roleId, managed: false }, { id: id(31), managed: true }, { id: id(32), managed: false }], [id(2)])).toEqual({ ids: [id(2), id(1)], method: "role-members" });
    expect(d.rest).toHaveBeenCalledTimes(2);
  });
  it.each([403, 500])("falls back to cache IDs after HTTP %s", async status => {
    const { d } = deps(); d.rest = async () => ({ status, body: {} });
    expect(await collectRoleCandidates(d, [], [id(1)])).toEqual({ ids: [id(1)], method: "cache", stopReason: "error" });
  });
  it("guards an unknown role envelope and falls back", async () => {
    const { d } = deps(); d.rest = async () => ({ status: 200, body: { unknown: 3 } });
    expect(await collectRoleCandidates(d, [], [id(1)])).toEqual({ ids: [id(1)], method: "cache", stopReason: "error" });
  });
  it("keeps role candidates and reserves the shared budget for refreshing them", async () => {
    const { d, emit } = deps();
    d.pacer = createPacer({ maxCalls: 4, restIntervalMs: 0, gatewayIntervalMs: 0 });
    d.rest = vi.fn(async kind => kind === "role-counts"
      ? { status: 200, body: { [id(30)]: 1, [id(31)]: 1, [id(32)]: 1 } }
      : { status: 200, body: [id(1)] });
    const candidates = await collectRoleCandidates(d,
      [30, 31, 32].map(n => ({ id: id(n), managed: false })), []);
    expect(candidates).toEqual({ ids: [id(1)], method: "role-members", stopReason: "call_cap" });
    expect(d.pacer.callsRemaining).toBe(1);
    d.dispatchMembers = async () => emit({ guildId, nonce: "batch-nonce", members: [raw(1)] });
    expect((await collectRefresh(d, candidates.ids, candidates.method)).members).toHaveLength(1);
    expect(d.pacer.callsUsed).toBe(4);
  });
});

describe("gateway refresh collector", () => {
  it("subscribes before dispatch and only uses fresh nonce-matching requested members", async () => {
    const { d, emit, unsubscribe } = deps();
    d.dispatchMembers = vi.fn(async () => {
      emit({ chunks: [{ guildId, nonce: "old", members: [raw(1, "stale")] }] });
      emit({ chunks: [{ guildId: id(99), nonce: "batch-nonce", members: [raw(1, "otherguild")] }] });
      emit({ chunks: [{ guildId, nonce: "batch-nonce", members: [raw(1, "fresh"), raw(1, "duplicate"), raw(99)], notFound: [id(2)] }] });
    });
    const result = await collectRefresh(d, [id(1), id(2)], "cache");
    expect(result.members).toHaveLength(1); expect(result.members[0]?.username).toBe("fresh");
    expect(result.expected).toBeNull(); expect(result.method).toBe("cache"); expect(unsubscribe).toHaveBeenCalledOnce();
  });
  it("accepts the raw gateway guild_id/not_found envelope", async () => {
    const { d, emit } = deps(); d.dispatchMembers = async () => emit({ guild_id: guildId, nonce: "batch-nonce", members: [raw(1)], not_found: [id(2)] });
    expect((await collectRefresh(d, [id(1), id(2)], "role-members")).members).toHaveLength(1);
  });
  it("fails closed when the runtime does not return the nonce", async () => {
    const { d, emit, unsubscribe } = deps(); d.dispatchMembers = async () => emit({ chunks: [{ guildId, members: [raw(1)] }] });
    await expect(collectRefresh(d, [id(1)], "cache", 5)).rejects.toBeInstanceOf(DiscordProtocolError);
    expect(unsubscribe).toHaveBeenCalledOnce();
  });
  it("keeps the first correlated batch when a later batch never replies", async () => {
    const { d, emit, unsubscribe } = deps();
    d.dispatchMembers = vi.fn().mockImplementationOnce(async () =>
      emit({ guildId, nonce: "batch-nonce", members: Array.from({ length: 100 }, (_, n) => raw(n + 1)) }))
      .mockResolvedValueOnce(undefined);
    const result = await collectRefresh(d, Array.from({ length: 101 }, (_, n) => id(n + 1)), "cache", 5);
    expect(result.members).toHaveLength(100);
    expect(unsubscribe).toHaveBeenCalledTimes(2);
    expect(computeCoverage(result.method, result.members.length, 100, result.stopReason).complete).toBe(false);
  });
  it("drops unresolved IDs and backs off before a following batch", async () => {
    const { d, emit } = deps();
    d.dispatchMembers = vi.fn(async ids => emit({ chunks: [{ guildId, nonce: "batch-nonce", members: [raw(Number(BigInt(ids[0]!) - 200000000000000000n))] }] }));
    const result = await collectRefresh(d, Array.from({ length: 101 }, (_, n) => id(n + 1)), "role-members", 5);
    expect(result.members.map(m => m.userId)).toEqual([id(1), id(101)]);
    expect(d.pacer.beforeGateway).toHaveBeenCalledTimes(2); expect(d.pacer.afterGatewayTimeout).toHaveBeenCalledOnce();
  });
  it("cleans the subscription and timer when dispatch rejects", async () => {
    const { d, unsubscribe, controller } = deps(); d.dispatchMembers = async () => { throw new Error("dispatch unavailable"); };
    await expect(collectRefresh(d, [id(1)], "cache")).rejects.toThrow("dispatch unavailable");
    expect(unsubscribe).toHaveBeenCalledOnce(); expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
  });
  it("aborts waiting for a gateway response and never reads member cache", async () => {
    const { d, controller, unsubscribe } = deps();
    const promise = expect(collectRefresh(d, [id(1)], "cache")).rejects.toBeInstanceOf(AbortedError);
    await Promise.resolve(); await Promise.resolve(); controller.abort(); await promise;
    expect(unsubscribe).toHaveBeenCalledOnce(); expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
  });
  it("keeps correlated members when the deadline ends the batch", async () => {
    vi.useFakeTimers(); const { d, emit, unsubscribe } = deps(); d.deadline = Date.now() + 100;
    d.dispatchMembers = async () => emit({ chunks: [{ guildId, nonce: "batch-nonce", members: [raw(1)] }] });
    const promise = collectRefresh(d, [id(1), id(2)], "cache"); await vi.advanceTimersByTimeAsync(100);
    const result = await promise; expect(result.stopReason).toBe("deadline"); expect(result.members).toHaveLength(1); expect(unsubscribe).toHaveBeenCalledOnce();
  });
});

describe("collection async boundaries", () => {
  it("clears abort listeners on success without creating an infinite timer", async () => {
    const controller = new AbortController();
    expect(await guarded(Promise.resolve(3), controller.signal, Infinity)).toBe(3);
    expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
  });
  it("cancels the batch pause promptly", async () => {
    const controller = new AbortController();
    const pending = expect(collectionSleep(5000, controller.signal)).rejects.toBeInstanceOf(AbortedError);
    controller.abort(); await pending; expect(getEventListeners(controller.signal, "abort")).toHaveLength(0);
  });
});
