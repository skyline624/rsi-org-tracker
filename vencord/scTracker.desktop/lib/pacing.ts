/** Thrown when a guild's collection reached its call budget: send what was collected, as partial. */
export class CapReachedError extends Error {
  constructor(message = "Plafond d'appels atteint : envoi partiel") {
    super(message);
    this.name = "CapReachedError";
  }
}

/** Thrown once the user pressed « Arrêter »: nothing is sent. */
export class AbortedError extends Error {
  constructor(message = "Collecte arrêtée : rien n'a été envoyé") {
    super(message);
    this.name = "AbortedError";
  }
}

/** Thrown when collection approaches the tracker's 30 minute duration limit. */
export class DurationReachedError extends Error {
  constructor(message = "Durée maximale de collecte atteinte : envoi partiel") {
    super(message);
    this.name = "DurationReachedError";
  }
}

/** Leaves five minutes for finalization before the API's 30 minute collection-duration limit. */
export const DEFAULT_MAX_DURATION_MS = 25 * 60 * 1000;

export type Pacer = {
  /** Waits until 1.2 s after the previous Discord REST call started, then counts one call. */
  beforeRest(): Promise<void>;
  /** Waits until 1 s after the previous gateway batch started, then counts one call. */
  beforeGateway(): Promise<void>;
  /** Waits 30 s after a gateway batch got no full answer. Call it only when another batch follows. */
  afterGatewayTimeout(): Promise<void>;
  /** Check after Discord answers; a duration error means finish collection as partial. */
  check(): void;
  /** Check immediately before IPC, allowing a valid partial collection after its deadline. */
  checkCancellation(): void;
  readonly callsUsed: number;
  readonly callsRemaining: number;
};

/**
 * Paces one guild's collection (spec § 5.4): at least 1.2 s between two REST calls, 1 s between
 * two gateway batches, 30 s after a batch that timed out, and at most 200 calls in all, each REST
 * request and each gateway batch counting for one. Every method throws AbortedError as soon as
 * the signal aborts, even in the middle of a wait. Abort listeners and timers exist only during
 * each wait and are removed on every exit. Reservations are serialized to preserve the budget
 * and intervals for simultaneous calls. `now` and `sleep` are injectable for tests.
 */
export function createPacer(o: {
  restIntervalMs?: number;
  gatewayIntervalMs?: number;
  gatewayBackoffMs?: number;
  maxCalls?: number;
  maxDurationMs?: number;
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
  signal?: AbortSignal;
} = {}): Pacer {
  const restInterval = o.restIntervalMs ?? 1200;
  const gatewayInterval = o.gatewayIntervalMs ?? 1000;
  const gatewayBackoff = o.gatewayBackoffMs ?? 30_000;
  const maxCalls = o.maxCalls ?? 200;
  const maxDuration = o.maxDurationMs ?? DEFAULT_MAX_DURATION_MS;
  const now = o.now ?? (() => performance.now());
  const startedAt = now();
  const signal = o.signal;

  let calls = 0;
  let lastRest: number | null = null;
  let lastGateway: number | null = null;
  let queue: Promise<void> = Promise.resolve();

  const checkCancellation = () => {
    if (signal?.aborted) throw new AbortedError();
  };
  const check = () => {
    checkCancellation();
    if (now() - startedAt >= maxDuration) throw new DurationReachedError();
  };
  const pause = async (ms: number) => {
    check();
    if (ms <= 0) return;
    const remaining = maxDuration - (now() - startedAt);
    const waitMs = Math.min(ms, remaining);
    await new Promise<void>((resolve, reject) => {
      let finished = false;
      let timer: ReturnType<typeof setTimeout> | undefined;
      const finish = (error?: unknown) => {
        if (finished) return;
        finished = true;
        if (timer !== undefined) clearTimeout(timer);
        signal?.removeEventListener("abort", onAbort);
        if (error !== undefined) reject(error);
        else resolve();
      };
      const onAbort = () => finish(new AbortedError());
      signal?.addEventListener("abort", onAbort, { once: true });
      if (signal?.aborted) {
        onAbort();
        return;
      }
      if (o.sleep !== undefined) {
        // An injected sleeper may finish after cancellation. It has no remaining listener.
        timer = setTimeout(() => finish(new DurationReachedError()), remaining);
        Promise.resolve().then(() => o.sleep!(waitMs)).then(() => finish(), error => finish(error));
      } else {
        timer = setTimeout(() => finish(), waitMs);
      }
    });
    check();
    if (ms >= remaining) throw new DurationReachedError();
  };
  const serialize = (action: () => Promise<void>): Promise<void> => {
    const result = queue.then(action);
    queue = result.catch(() => {});
    return result;
  };
  /** Checks the signal and budget, waits for the interval and reserves one call. */
  const take = async (kind: "rest" | "gateway") => {
    check();
    if (calls >= maxCalls) throw new CapReachedError();
    const last = kind === "rest" ? lastRest : lastGateway;
    const interval = kind === "rest" ? restInterval : gatewayInterval;
    if (last !== null) await pause(last + interval - now());
    check();
    calls++;
    if (kind === "rest") lastRest = now();
    else lastGateway = now();
  };

  return {
    beforeRest: () => serialize(() => take("rest")),
    beforeGateway: () => serialize(() => take("gateway")),
    afterGatewayTimeout: () => serialize(() => pause(gatewayBackoff)),
    check,
    checkCancellation,
    get callsUsed() {
      return calls;
    },
    get callsRemaining() { return Math.max(0, maxCalls - calls); },
  };
}
