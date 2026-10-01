import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { createAutoSync, MAX_AUTO_SYNC_MINUTES, type AutoSyncState } from "../scTracker.desktop/lib/autoSync";

const minute = 60_000;
function fixture() {
  let state: AutoSyncState = { enabled: false, running: false, nextRunAt: null };
  const d = { canRun: vi.fn(() => true), run: vi.fn(async () => {}), state: (value: AutoSyncState) => { state = value; }, error: vi.fn() };
  return { d, timer: createAutoSync(d), state: () => state };
}

describe("automatic sync timer", () => {
  beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(new Date("2026-10-01T12:00:00Z")); });
  afterEach(() => { vi.clearAllTimers(); vi.useRealTimers(); });

  it("waits a full interval before the first send and rearms after it finishes", async () => {
    const { d, timer, state } = fixture();
    const now = Date.now();
    timer.configure(true, 60);
    expect(state()).toEqual({ enabled: true, running: false, nextRunAt: now + 60 * minute });
    await vi.advanceTimersByTimeAsync(60 * minute - 1);
    expect(d.run).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(d.run).toHaveBeenCalledOnce();
    expect(state().nextRunAt).toBe(now + 120 * minute);
    await vi.advanceTimersByTimeAsync(60 * minute);
    expect(d.run).toHaveBeenCalledTimes(2);
  });

  it("does not schedule anything when disabled", async () => {
    const { d, timer, state } = fixture();
    timer.configure(false, 60);
    await vi.advanceTimersByTimeAsync(24 * 60 * minute);
    expect(d.run).not.toHaveBeenCalled();
    expect(state().nextRunAt).toBeNull();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each([undefined, "60", 0, 9, 10.5, MAX_AUTO_SYNC_MINUTES + 1, NaN, Infinity])("refuses an imported invalid interval %s", async minutes => {
    const { d, timer, state } = fixture();
    timer.configure(true, minutes);
    expect(state().enabled).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
    expect(d.run).not.toHaveBeenCalled();
  });

  it("skips a busy or unavailable occurrence without queuing a catch-up send", async () => {
    const { d, timer } = fixture();
    d.canRun.mockReturnValue(false);
    timer.configure(true, 10);
    await vi.advanceTimersByTimeAsync(10 * minute);
    expect(d.run).not.toHaveBeenCalled();
    d.canRun.mockReturnValue(true);
    await vi.advanceTimersByTimeAsync(10 * minute - 1);
    expect(d.run).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(d.run).toHaveBeenCalledOnce();
  });

  it("starts the next interval after a long-running batch rather than overlapping it", async () => {
    const { d, timer, state } = fixture();
    let finish!: () => void;
    d.run.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    timer.configure(true, 10);
    await vi.advanceTimersByTimeAsync(10 * minute);
    expect(state()).toEqual({ enabled: true, running: true, nextRunAt: null });
    await vi.advanceTimersByTimeAsync(60 * minute);
    expect(d.run).toHaveBeenCalledOnce();
    finish();
    await vi.advanceTimersByTimeAsync(0);
    expect(state().nextRunAt).toBe(Date.now() + 10 * minute);
  });

  it("uses the latest checked-server selection when the deadline arrives", async () => {
    const { d, timer } = fixture();
    let selected = ["first"];
    const batches: string[][] = [];
    d.run.mockImplementation(async () => { batches.push([...selected]); });
    timer.configure(true, 10);
    selected = ["second", "third"];
    await vi.advanceTimersByTimeAsync(10 * minute);
    expect(batches).toEqual([["second", "third"]]);
  });

  it("replaces the previous deadline when the interval changes", async () => {
    const { d, timer, state } = fixture();
    timer.configure(true, 60);
    await vi.advanceTimersByTimeAsync(10 * minute);
    timer.configure(true, 20);
    expect(vi.getTimerCount()).toBe(1);
    expect(state().nextRunAt).toBe(Date.now() + 20 * minute);
    await vi.advanceTimersByTimeAsync(20 * minute);
    expect(d.run).toHaveBeenCalledOnce();
  });

  it("stops a pending timer", async () => {
    const { d, timer, state } = fixture();
    timer.configure(true, 10);
    timer.stop();
    await vi.advanceTimersByTimeAsync(60 * minute);
    expect(d.run).not.toHaveBeenCalled();
    expect(state()).toEqual({ enabled: false, running: false, nextRunAt: null });
    expect(vi.getTimerCount()).toBe(0);
  });

  it("does not restart after a stopped in-flight run resolves", async () => {
    const { d, timer, state } = fixture();
    let finish!: () => void;
    d.run.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    timer.configure(true, 10);
    await vi.advanceTimersByTimeAsync(10 * minute);
    timer.stop();
    finish();
    await vi.advanceTimersByTimeAsync(60 * minute);
    expect(d.run).toHaveBeenCalledOnce();
    expect(state()).toEqual({ enabled: false, running: false, nextRunAt: null });
    expect(vi.getTimerCount()).toBe(0);
  });

  it("cannot overlap an old run after the timer is reconfigured", async () => {
    const { d, timer } = fixture();
    let finish!: () => void;
    d.run.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    timer.configure(true, 10);
    await vi.advanceTimersByTimeAsync(10 * minute);
    timer.configure(true, 20);
    await vi.advanceTimersByTimeAsync(20 * minute);
    expect(d.run).toHaveBeenCalledOnce();
    finish();
    await vi.advanceTimersByTimeAsync(0);
    expect(vi.getTimerCount()).toBe(1);
  });

  it("reports an unexpected error and leaves a future interval armed", async () => {
    const { d, timer, state } = fixture();
    const error = new Error("unexpected failure");
    d.run.mockRejectedValueOnce(error);
    timer.configure(true, 10);
    await vi.advanceTimersByTimeAsync(10 * minute);
    expect(d.error).toHaveBeenCalledWith(error);
    expect(state().running).toBe(false);
    await vi.advanceTimersByTimeAsync(10 * minute);
    expect(d.run).toHaveBeenCalledTimes(2);
  });

  it("sends once after a suspended clock resumes and schedules a fresh interval", async () => {
    const { d, timer, state } = fixture();
    timer.configure(true, 10);
    vi.setSystemTime(Date.now() + 24 * 60 * minute);
    await vi.runOnlyPendingTimersAsync();
    expect(d.run).toHaveBeenCalledOnce();
    expect(state().nextRunAt).toBe(Date.now() + 10 * minute);
  });
});
