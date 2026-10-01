import { getEventListeners } from "node:events";
import { describe, expect, it } from "vitest";

import { AbortedError, CapReachedError, createPacer, DurationReachedError } from "../scTracker.desktop/lib/pacing";

/** A clock that only moves when the pacer sleeps or the test says so. */
function fakeTime() {
  let t = 1_000_000;
  const sleeps: number[] = [];
  return {
    sleeps,
    now: () => t,
    sleep: async (ms: number) => {
      sleeps.push(ms);
      t += ms;
    },
    advance: (ms: number) => {
      t += ms;
    },
  };
}

describe("createPacer", () => {
  it("serializes simultaneous calls so they cannot bypass spacing or the shared budget", async () => {
    const time = fakeTime();
    const pacer = createPacer({ now: time.now, sleep: time.sleep, maxCalls: 2 });
    const outcomes = await Promise.allSettled([pacer.beforeRest(), pacer.beforeRest(), pacer.beforeGateway()]);
    expect(outcomes.slice(0, 2).map(r => r.status)).toEqual(["fulfilled", "fulfilled"]);
    expect(outcomes[2]).toMatchObject({ status: "rejected", reason: expect.any(CapReachedError) });
    expect(time.sleeps).toEqual([1200]);
    expect(pacer.callsUsed).toBe(2);
  });

  it("ends a wait at the duration limit without reserving another call", async () => {
    const time = fakeTime();
    const pacer = createPacer({ now: time.now, sleep: time.sleep, maxDurationMs: 500 });
    await pacer.beforeRest();
    await expect(pacer.beforeRest()).rejects.toBeInstanceOf(DurationReachedError);
    expect(time.sleeps).toEqual([500]);
    expect(pacer.callsUsed).toBe(1);
  });

  it("checks cancellation and duration again after an external request", async () => {
    const time = fakeTime();
    const controller = new AbortController();
    const pacer = createPacer({ now: time.now, sleep: time.sleep, signal: controller.signal, maxDurationMs: 100 });
    await pacer.beforeRest();
    time.advance(100);
    expect(() => pacer.check()).toThrow(DurationReachedError);
    expect(() => pacer.checkCancellation()).not.toThrow();
    controller.abort();
    expect(() => pacer.check()).toThrow(AbortedError);
    expect(() => pacer.checkCancellation()).toThrow(AbortedError);
  });

  it("interrupts an injected sleeper that never resolves when the deadline arrives", async () => {
    const pacer = createPacer({ maxDurationMs: 30, sleep: () => new Promise<void>(() => {}) });
    await expect(pacer.afterGatewayTimeout()).rejects.toBeInstanceOf(DurationReachedError);
    expect(pacer.callsUsed).toBe(0);
  });

  it("removes abort listeners after successful, failed and cancelled waits", async () => {
    const time = fakeTime();
    const controller = new AbortController();
    const signal = controller.signal;
    const successful = createPacer({ now: time.now, sleep: time.sleep, signal });
    await successful.afterGatewayTimeout();
    expect(getEventListeners(signal, "abort")).toEqual([]);

    const failed = createPacer({ sleep: async () => { throw new Error("sleep failed"); }, signal });
    await expect(failed.afterGatewayTimeout()).rejects.toThrow("sleep failed");
    expect(getEventListeners(signal, "abort")).toEqual([]);

    const pending = createPacer({ sleep: () => new Promise<void>(() => {}), signal });
    const waiting = pending.afterGatewayTimeout();
    await Promise.resolve();
    controller.abort();
    await expect(waiting).rejects.toBeInstanceOf(AbortedError);
    expect(getEventListeners(signal, "abort")).toEqual([]);
  });

  it("spaces REST calls by 1200 ms from the start of the previous one", async () => {
    const time = fakeTime();
    const pacer = createPacer({ now: time.now, sleep: time.sleep });

    await pacer.beforeRest();
    await pacer.beforeRest();
    time.advance(500); // the request took 500 ms
    await pacer.beforeRest();
    time.advance(5000);
    await pacer.beforeRest();

    expect(time.sleeps).toEqual([1200, 700]);
    expect(pacer.callsUsed).toBe(4);
  });

  it("spaces gateway batches by 1000 ms, independently of REST calls", async () => {
    const time = fakeTime();
    const pacer = createPacer({ now: time.now, sleep: time.sleep });

    await pacer.beforeRest();
    await pacer.beforeGateway();
    await pacer.beforeGateway();

    expect(time.sleeps).toEqual([1000]);
    expect(pacer.callsUsed).toBe(3);
  });

  it("waits 30 s after a gateway batch timed out, without counting a call", async () => {
    const time = fakeTime();
    const pacer = createPacer({ now: time.now, sleep: time.sleep });

    await pacer.beforeGateway();
    time.advance(10_000); // the batch timed out
    await pacer.afterGatewayTimeout();
    await pacer.beforeGateway();

    expect(time.sleeps).toEqual([30_000]);
    expect(pacer.callsUsed).toBe(2);
  });

  it("stops at the 201st call, REST and gateway together", async () => {
    const time = fakeTime();
    const pacer = createPacer({ now: time.now, sleep: time.sleep });

    for (let i = 0; i < 100; i++) {
      await pacer.beforeRest();
      await pacer.beforeGateway();
    }
    const sleepsAtCap = time.sleeps.length;

    await expect(pacer.beforeRest()).rejects.toBeInstanceOf(CapReachedError);
    await expect(pacer.beforeGateway()).rejects.toBeInstanceOf(CapReachedError);
    expect(pacer.callsUsed).toBe(200);
    expect(time.sleeps).toHaveLength(sleepsAtCap);
  });

  it("uses the given intervals and cap", async () => {
    const time = fakeTime();
    const pacer = createPacer({
      now: time.now, sleep: time.sleep, restIntervalMs: 50, gatewayIntervalMs: 20, gatewayBackoffMs: 7, maxCalls: 3,
    });

    await pacer.beforeRest();
    await pacer.beforeRest();
    await pacer.beforeGateway();
    await pacer.afterGatewayTimeout();

    await expect(pacer.beforeGateway()).rejects.toBeInstanceOf(CapReachedError);
    expect(time.sleeps).toEqual([50, 7]);
  });

  it("refuses every call once aborted, before sleeping or counting", async () => {
    const time = fakeTime();
    const controller = new AbortController();
    const pacer = createPacer({ now: time.now, sleep: time.sleep, signal: controller.signal });
    await pacer.beforeRest();

    controller.abort();

    await expect(pacer.beforeRest()).rejects.toBeInstanceOf(AbortedError);
    await expect(pacer.beforeGateway()).rejects.toBeInstanceOf(AbortedError);
    await expect(pacer.afterGatewayTimeout()).rejects.toBeInstanceOf(AbortedError);
    expect(pacer.callsUsed).toBe(1);
    expect(time.sleeps).toEqual([]);
  });

  it("interrupts a wait in progress when aborted", async () => {
    const time = fakeTime();
    const controller = new AbortController();
    const pacer = createPacer({ now: time.now, sleep: () => new Promise<void>(() => {}), signal: controller.signal });
    await pacer.beforeRest();

    const waiting = pacer.beforeRest();
    controller.abort();

    await expect(waiting).rejects.toBeInstanceOf(AbortedError);
    expect(pacer.callsUsed).toBe(1);
  });

  it("interrupts its own timer when aborted", async () => {
    const controller = new AbortController();
    const pacer = createPacer({ signal: controller.signal });
    const started = Date.now();

    const waiting = pacer.afterGatewayTimeout();
    await Promise.resolve();
    expect(getEventListeners(controller.signal, "abort")).toHaveLength(1);
    controller.abort();

    await expect(waiting).rejects.toBeInstanceOf(AbortedError);
    expect(Date.now() - started).toBeLessThan(1000);
    expect(getEventListeners(controller.signal, "abort")).toEqual([]);
  });

  it("gives French messages", () => {
    expect(new CapReachedError().message).toBe("Plafond d'appels atteint : envoi partiel");
    expect(new AbortedError().message).toBe("Collecte arrêtée : rien n'a été envoyé");
  });
});
