import * as DataStore from "@api/DataStore";
import { Settings, SettingsStore } from "@api/Settings";
import type { PluginNative } from "@utils/types";
import { showToast, Toasts } from "@webpack/common";

import { collectGuild } from "./collect/strategy";
import { settings } from "./configuration";
import { createAutoSync, type AutoSyncState } from "./lib/autoSync";
import { bindCredentials, credentialsForSettings, savedCredentials } from "./lib/credentials";
import { snowflake } from "./lib/discordProtocol";
import { createSyncRunner, type RunnerState, type SyncConfig, type SyncStatus } from "./lib/syncRunner";

const KEY = "ScTracker_apiKey";
const CONNECTION = "ScTracker_connection";
let enabled = false;
let current: RunnerState = { busy: false, phase: "idle" };
// Feedback belongs to this Discord session; roster data and history belong to the tracker.
let lastResults: Record<string, SyncStatus> = {};
let automatic: AutoSyncState = { enabled: false, running: false, nextRunAt: null };
const listeners = new Set<() => void>();
let lastProgressToast = 0;
let keyWriteInProgress = false;
let configurationRevision = 0;

export const getRunnerState = () => ({ ...current, lastResults, automatic });
export function subscribeRunner(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
function notifyState() { for (const listener of listeners) listener(); }
export function trackedGuildIds(): string[] {
  const ids: unknown = settings.store.trackedGuildIds;
  return Array.isArray(ids) ? [...new Set(ids.filter(snowflake))] : [];
}
export const isTracked = (id: string) => trackedGuildIds().includes(id);
export function trackGuild(id: string, tracked: boolean) {
  if (!snowflake(id)) return;
  settings.store.trackedGuildIds = tracked ? [...new Set([...trackedGuildIds(), id])] : trackedGuildIds().filter(g => g !== id);
}
export async function loadApiKey() {
  const saved = savedCredentials(await DataStore.get(CONNECTION));
  if (saved) return saved.apiKey;
  const legacy: unknown = await DataStore.get(KEY);
  return typeof legacy === "string" ? legacy : "";
}
export async function saveConfiguration(config: SyncConfig | null) {
  if (keyWriteInProgress || runner.busy) throw new Error("Une opération est déjà en cours.");
  keyWriteInProgress = true;
  configurationRevision++;
  try {
    if (config) {
      const bound = bindCredentials(config);
      await DataStore.set(CONNECTION, bound);
      settings.store.trackerUrl = bound.url;
      settings.store.fingerprint = bound.fingerprint;
    } else await DataStore.del(CONNECTION);
    await DataStore.del(KEY);
  }
  finally { keyWriteInProgress = false; configurationRevision++; }
}

const nativeHelper = (): PluginNative<typeof import("./native")> => {
  const native = VencordNative.pluginHelpers.ScTracker as PluginNative<typeof import("./native")> | undefined;
  if (!native || typeof native.postSync !== "function") throw new Error("Le pont desktop SC Tracker est absent. Reconstruis Vencord puis redémarre Discord.");
  return native;
};

export const runner = createSyncRunner({
  async config() {
    if (!enabled) throw new Error("Active SC Tracker avant d'envoyer.");
    if (keyWriteInProgress) throw new Error("L'enregistrement de la clé locale est en cours. Réessaie ensuite.");
    nativeHelper(); // Fail before doing a costly collection when the desktop build is incomplete.
    const revision = configurationRevision;
    const stored: unknown = await DataStore.get(CONNECTION);
    if (revision !== configurationRevision || keyWriteInProgress) throw new Error("La configuration a changé pendant sa lecture. Réessaie.");
    return credentialsForSettings(stored, settings.store.trackerUrl, settings.store.fingerprint);
  },
  isTracked,
  collect: collectGuild,
  post: args => nativeHelper().postSync(args),
  status(guildId, status) {
    if (!enabled) return;
    lastResults = { ...lastResults, [guildId]: status };
    notifyState();
  },
  state(state) {
    current = state;
    notifyState();
    if (state.phase === "collecting" && state.progress && Date.now() - lastProgressToast >= 5000) {
      lastProgressToast = Date.now();
      showToast(`SC Tracker : ${state.progress}`, Toasts.Type.CLOCK, { duration: 5000 });
    }
  },
  notify(message, ok) { showToast(message, ok === true ? Toasts.Type.SUCCESS : ok === false ? Toasts.Type.FAILURE : Toasts.Type.MESSAGE, { duration: 10_000 }); },
});

const autoSync = createAutoSync({
  canRun: () => enabled && !runner.busy && !keyWriteInProgress && trackedGuildIds().length > 0,
  run: () => runner.run(trackedGuildIds()),
  state(state) { automatic = state; notifyState(); },
  error() { showToast("SC Tracker : l'envoi automatique a échoué. Réessaie depuis le panneau.", Toasts.Type.FAILURE); },
});

function updateAutoSync() {
  autoSync.configure(enabled && settings.store.autoSyncEnabled === true, settings.store.autoSyncIntervalMinutes);
}

function settingsChanged(_data: unknown, path: string) {
  if (path === "" || path === "plugins.ScTracker"
    || path === "plugins.ScTracker.autoSyncEnabled" || path === "plugins.ScTracker.autoSyncIntervalMinutes") updateAutoSync();
}

/** The visible stop button pauses the timer as well as the current collection/batch. */
export function stopSending() {
  settings.store.autoSyncEnabled = false;
  autoSync.stop();
  runner.stop();
}

export function start() {
  // Remove summaries saved by previous versions, including from Cloud Sync/imports.
  const legacy = Settings.plugins.ScTracker;
  if (legacy) delete legacy.lastResults;
  lastResults = {};
  enabled = true;
  SettingsStore.removeGlobalChangeListener(settingsChanged);
  SettingsStore.addGlobalChangeListener(settingsChanged);
  updateAutoSync();
  notifyState();
}
export function stop() {
  enabled = false;
  SettingsStore.removeGlobalChangeListener(settingsChanged);
  autoSync.stop();
  runner.stop();
  lastResults = {};
  notifyState();
}
