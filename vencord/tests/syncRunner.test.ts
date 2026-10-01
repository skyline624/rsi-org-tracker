import { describe, expect, it, vi } from "vitest";

import { createSyncRunner, type SyncRunDeps, type SyncStatus } from "../scTracker.desktop/lib/syncRunner";

const gid = "100000000000000001";
const other = "100000000000000002";
function fixture() {
  const statuses = new Map<string, SyncStatus>();
  const tracked = new Set([gid, other]);
  const d: SyncRunDeps = {
    config: vi.fn(async () => ({ url: "https://127.0.0.1", fingerprint: "A".repeat(64), apiKey: "test-key" })),
    isTracked: id => tracked.has(id),
    collect: vi.fn<SyncRunDeps["collect"]>(async id => ({
      method: "member-search", expected: 1, stopReason: "exhausted", durationMs: 1, collectedAt: new Date().toISOString(),
      guild: { id, name: "Test", icon: null, memberCount: 1 }, roles: [],
      members: [{ userId: "200000000000000001", username: "test", globalName: null, nick: null, roleIds: [], joinedAt: null, bot: false }],
    })),
    post: vi.fn(async () => ({ status: 200, body: JSON.stringify({ isComplete: true, membersReceived: 1, orgSid: "TEST" }), retryAfter: null })),
    status: (id, status) => { statuses.set(id, status); }, state: vi.fn(), notify: vi.fn(), batchDelayMs: 0,
  };
  return { d, statuses, tracked, runner: createSyncRunner(d) };
}

describe("shared sync runner", () => {
  it("deduplicates tracked guilds, sends sequentially and reports only a summary", async () => {
    const { d, runner, statuses } = fixture(); await runner.run([gid, gid, "not-tracked", other]);
    expect(d.collect).toHaveBeenCalledTimes(2); expect(d.post).toHaveBeenCalledTimes(2);
    const body = JSON.parse(vi.mocked(d.post).mock.calls[0]![0].body);
    expect(body.coverage.complete).toBe(true); expect(body.guild.id).toBe(gid);
    expect(statuses.get(gid)).toMatchObject({ state: "sent", method: "member-search", count: 1, complete: true, orgSid: "TEST" });
    expect(JSON.stringify([...statuses.values()])).not.toContain("test-key"); expect(runner.busy).toBe(false);
  });
  it.each([true, false])("preflights invalid configuration before collection (requireTracked: %s)", async requireTracked => {
    const { d, runner, statuses } = fixture(); d.config = async () => ({ url: "http://unsafe", fingerprint: "invalid", apiKey: "" });
    await runner.run([gid, other], { requireTracked }); expect(d.collect).not.toHaveBeenCalled(); expect(d.post).not.toHaveBeenCalled();
    expect(statuses.get(gid)?.state).toBe("not_sent"); expect(statuses.get(other)?.state).toBe("not_sent");
  });
  it("refuses an empty partial collection", async () => {
    const { d, runner } = fixture(); const original = d.collect;
    d.collect = async (...args) => ({ ...await original(...args), members: [], stopReason: "call_cap" });
    await runner.run([gid]); expect(d.post).not.toHaveBeenCalled();
  });
  it("rechecks the bound configuration after collecting and before IPC", async () => {
    const { d, runner } = fixture();
    d.config = vi.fn().mockResolvedValueOnce({ url: "https://127.0.0.1", fingerprint: "A".repeat(64), apiKey: "test-key" })
      .mockResolvedValueOnce({ url: "https://another.example", fingerprint: "B".repeat(64), apiKey: "test-key" });
    await runner.run([gid]); expect(d.collect).toHaveBeenCalledOnce(); expect(d.post).not.toHaveBeenCalled();
  });
  it("enforces a single job through collection and upload", async () => {
    const { d, runner } = fixture(); let finish!: (reply: Awaited<ReturnType<SyncRunDeps["post"]>>) => void;
    d.post = vi.fn<SyncRunDeps["post"]>(() => new Promise(r => { finish = r; }));
    const pending = runner.run([gid]); await vi.waitFor(() => expect(d.post).toHaveBeenCalledOnce());
    await runner.run([other], { requireTracked: false }); expect(d.collect).toHaveBeenCalledOnce(); expect(runner.busy).toBe(true);
    finish({ status: 200, body: "{}", retryAfter: null }); await pending; expect(runner.busy).toBe(false);
  });
  it("cancellation after an uninterruptible collect resolves prevents IPC", async () => {
    const { d, runner, statuses } = fixture(); const original = d.collect;
    d.collect = async (...args) => { const value = await original(...args); runner.stop(); return value; };
    await runner.run([gid, other]); expect(d.post).not.toHaveBeenCalled(); expect(statuses.get(gid)?.state).toBe("not_sent"); expect(statuses.get(other)?.state).toBe("not_sent");
  });
  it("checks cancellation immediately after the final state update before IPC", async () => {
    const { d, runner } = fixture(); d.state = state => { if (state.phase === "uploading") runner.stop(); };
    await runner.run([gid]); expect(d.post).not.toHaveBeenCalled();
  });
  it("stop during upload preserves its reported outcome and skips remaining guilds", async () => {
    const { d, runner, statuses } = fixture(); let finish!: (reply: Awaited<ReturnType<SyncRunDeps["post"]>>) => void;
    d.post = vi.fn<SyncRunDeps["post"]>(() => new Promise(r => { finish = r; }));
    const pending = runner.run([gid, other]); await vi.waitFor(() => expect(d.post).toHaveBeenCalledOnce()); runner.stop();
    finish({ status: 200, body: "{}", retryAfter: null }); await pending;
    expect(statuses.get(gid)?.state).toBe("sent"); expect(statuses.get(other)?.state).toBe("not_sent"); expect(d.collect).toHaveBeenCalledOnce();
    expect(d.notify).toHaveBeenCalledWith(expect.stringContaining("peut être enregistré"));
  });
  it.each([401, 403, 429, 503])("stops a batch after tracker HTTP %s", async status => {
    const { d, runner, statuses } = fixture(); d.post = vi.fn(async () => ({ status, body: "{}", retryAfter: 30 }));
    await runner.run([gid, other]); expect(d.collect).toHaveBeenCalledOnce(); expect(statuses.get(other)?.state).toBe("not_sent");
  });
  it.each([400, 413, 409])("continues a batch after guild-specific HTTP %s", async status => {
    const { d, runner } = fixture(); d.post = vi.fn(async () => ({ status, body: '{"code":"stale_sync"}', retryAfter: null }));
    await runner.run([gid, other]); expect(d.collect).toHaveBeenCalledTimes(2);
  });
  it("a rejected native bridge is an uncertain upload and stops the batch", async () => {
    const { d, runner, statuses } = fixture(); d.post = vi.fn(async () => { throw new Error("IPC disconnected"); });
    await runner.run([gid, other]); expect(d.collect).toHaveBeenCalledOnce();
    expect(statuses.get(gid)?.message).toContain("peut-être été enregistré"); expect(statuses.get(other)?.state).toBe("not_sent");
  });
  it("allows a valid partial payload after the 25-minute collection deadline", async () => {
    const { d, runner } = fixture(); const original = d.collect;
    d.collect = async (...args) => ({ ...await original(...args), stopReason: "deadline", durationMs: 1_500_000 });
    await runner.run([gid]); expect(d.post).toHaveBeenCalledOnce();
    expect(JSON.parse(vi.mocked(d.post).mock.calls[0]![0].body).coverage.complete).toBe(false);
  });
  it("does not send data beyond the API's maximum collection age", async () => {
    const { d, runner } = fixture(); const original = d.collect;
    d.collect = async (...args) => ({ ...await original(...args), durationMs: 1_800_001 });
    await runner.run([gid]); expect(d.post).not.toHaveBeenCalled();
  });
  it("a guild removed during collection is not sent", async () => {
    const { d, runner, tracked, statuses } = fixture(); const original = d.collect;
    d.collect = async (...args) => { tracked.delete(gid); return original(...args); };
    await runner.run([gid]); expect(d.post).not.toHaveBeenCalled(); expect(statuses.get(gid)?.state).toBe("not_sent");
  });
  it("sends an unchecked guild once without adding it to the automatic selection", async () => {
    const { d, runner, tracked, statuses } = fixture();
    tracked.delete(gid);
    await runner.run([gid], { requireTracked: false });
    expect(d.collect).toHaveBeenCalledOnce();
    expect(d.post).toHaveBeenCalledWith(expect.objectContaining({ guildId: gid }));
    expect(statuses.get(gid)?.state).toBe("sent");
    expect([...tracked]).toEqual([other]);
    await runner.run([gid]);
    expect(d.post).toHaveBeenCalledOnce();
  });
  it("keeps a one-off manual request when the automatic checkbox changes during collection", async () => {
    const { d, runner, tracked } = fixture(); const original = d.collect;
    d.collect = async (...args) => { tracked.delete(gid); return original(...args); };
    await runner.run([gid], { requireTracked: false });
    expect(d.post).toHaveBeenCalledOnce();
    expect(tracked.has(gid)).toBe(false);
  });
  it("still cancels an unchecked manual collection before IPC", async () => {
    const { d, runner, tracked } = fixture(); const original = d.collect;
    tracked.delete(gid);
    d.collect = async (...args) => { const result = await original(...args); runner.stop(); return result; };
    await runner.run([gid], { requireTracked: false });
    expect(original).toHaveBeenCalledOnce();
    expect(d.post).not.toHaveBeenCalled();
  });
  it("rejects an invalid manual guild id before reading configuration or collecting", async () => {
    const { d, runner } = fixture();
    await runner.run(["../other"], { requireTracked: false });
    expect(d.config).not.toHaveBeenCalled();
    expect(d.collect).not.toHaveBeenCalled();
    expect(d.post).not.toHaveBeenCalled();
  });
  it("rechecks automatic selection after the final asynchronous configuration read", async () => {
    const { d, runner, tracked } = fixture();
    const original = d.config;
    let reads = 0;
    d.config = async () => { const result = await original(); if (++reads === 2) tracked.delete(gid); return result; };
    await runner.run([gid]);
    expect(d.collect).toHaveBeenCalledOnce();
    expect(d.post).not.toHaveBeenCalled();
  });
  it("stopping while reading local configuration releases the active job", async () => {
    const { d, runner } = fixture(); d.config = () => new Promise(() => {});
    const promise = runner.run([gid]); runner.stop(); await promise; expect(runner.busy).toBe(false); expect(d.collect).not.toHaveBeenCalled();
  });
});
