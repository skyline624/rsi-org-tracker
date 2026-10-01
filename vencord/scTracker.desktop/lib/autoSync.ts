export const DEFAULT_AUTO_SYNC_MINUTES = 60;
export const MIN_AUTO_SYNC_MINUTES = 10;
export const MAX_AUTO_SYNC_MINUTES = 7 * 24 * 60;

export function validAutoSyncMinutes(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value)
    && value >= MIN_AUTO_SYNC_MINUTES && value <= MAX_AUTO_SYNC_MINUTES;
}

export type AutoSyncState = { enabled: boolean; running: boolean; nextRunAt: number | null };
type AutoSyncDeps = {
  canRun(): boolean;
  run(): Promise<void>;
  state(state: AutoSyncState): void;
  error(error: unknown): void;
};

/** Session-only timer: no immediate send, overlapping run, missed-run queue or saved deadline. */
export function createAutoSync(d: AutoSyncDeps) {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let revision = 0;
  let running = false;
  let active = false;
  let minutes = DEFAULT_AUTO_SYNC_MINUTES;
  let nextRunAt: number | null = null;
  const publish = () => d.state({ enabled: active, running, nextRunAt });
  const clear = () => { clearTimeout(timer); timer = undefined; nextRunAt = null; };

  function schedule(generation: number) {
    if (!active || generation !== revision) return;
    const delay = minutes * 60_000;
    nextRunAt = Date.now() + delay;
    timer = setTimeout(() => void tick(generation), delay);
    publish();
  }

  async function tick(generation: number) {
    if (!active || generation !== revision) return;
    clear();
    publish();
    try {
      // A manual send, missing selection or credentials update skips this occurrence.
      if (!running && d.canRun()) {
        running = true;
        publish();
        try { await d.run(); }
        finally { running = false; }
      }
    } catch (error) { d.error(error); }
    finally {
      if (active && generation === revision) schedule(generation);
      else publish();
    }
  }

  return {
    configure(enabled: boolean, intervalMinutes: unknown) {
      revision++;
      clear();
      active = enabled && validAutoSyncMinutes(intervalMinutes);
      if (active) { minutes = intervalMinutes as number; schedule(revision); }
      else publish();
    },
    stop() { revision++; clear(); active = false; publish(); },
  };
}
