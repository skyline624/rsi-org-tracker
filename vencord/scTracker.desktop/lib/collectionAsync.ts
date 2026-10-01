import { AbortedError, DurationReachedError } from "./pacing";

/** Bounds a Discord operation without assuming its internal request supports AbortSignal. */
export function guarded<T>(work: Promise<T>, signal: AbortSignal, deadline: number, now = () => performance.now()): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let done = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const finish = (ok: boolean, value: unknown) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      signal.removeEventListener("abort", abort);
      if (ok) resolve(value as T);
      else reject(value);
    };
    const abort = () => finish(false, new AbortedError());
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
    else if (now() >= deadline) finish(false, new DurationReachedError());
    else if (Number.isFinite(deadline)) timer = setTimeout(() => finish(false, new DurationReachedError()), deadline - now());
    // Attach handlers even after early cancellation so a late rejection is always consumed.
    work.then(value => {
      if (signal.aborted) abort();
      else if (now() >= deadline) finish(false, new DurationReachedError());
      else finish(true, value);
    }, error => finish(false, error));
  });
}

/** A pause that clears its timer and listeners on cancellation or collection deadline. */
export function collectionSleep(ms: number, signal: AbortSignal, deadline = Infinity, now = () => performance.now()): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const finish = (error?: Error) => {
      clearTimeout(timer);
      signal.removeEventListener("abort", abort);
      if (error) reject(error);
      else resolve();
    };
    const abort = () => finish(new AbortedError());
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
    else if (now() >= deadline) finish(new DurationReachedError());
    else {
      const remaining = deadline - now();
      timer = setTimeout(() => finish(ms >= remaining ? new DurationReachedError() : undefined), Math.min(ms, remaining));
    }
  });
}
