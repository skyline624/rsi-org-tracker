import { collectionSleep, guarded } from "./collectionAsync";
import type { CollectionResult } from "./collection";
import { computeCoverage } from "./coverage";
import { describePayloadRefusal, describeResult, type Outcome } from "./errors";
import { snowflake } from "./discordProtocol";
import { AbortedError } from "./pacing";
import { buildPayload, type SyncPayload, type SyncMethod } from "./payload";
import type { PostResult } from "./pinnedPost";
import { validatePostArgs } from "./postArgs";

export type SyncStatus = {
  at: string;
  state: "sent" | "failed" | "not_sent";
  message: string;
  method?: SyncMethod;
  complete?: boolean;
  count?: number;
  orgSid?: string | null;
};
export type RunnerState = { busy: boolean; phase: "idle" | "collecting" | "uploading" | "waiting"; guildId?: string; progress?: string };
export type SyncConfig = { url: string; fingerprint: string; apiKey: string };
export type SyncRunDeps = {
  config(): Promise<SyncConfig>;
  isTracked(guildId: string): boolean;
  collect(guildId: string, signal: AbortSignal, progress: (method: SyncMethod, count: number) => void): Promise<CollectionResult & { guild: SyncPayload["guild"]; roles: SyncPayload["roles"]; durationMs: number; collectedAt: string }>;
  post(args: { url: string; fingerprint: string; apiKey: string; guildId: string; body: string }): Promise<PostResult>;
  status(guildId: string, status: SyncStatus): void;
  state(state: RunnerState): void;
  notify(message: string, ok?: boolean): void;
  batchDelayMs?: number;
};

/** Shared runner for settings and context menus. It holds one active job through upload. */
export function createSyncRunner(d: SyncRunDeps) {
  let controller: AbortController | null = null;
  let phase: RunnerState["phase"] = "idle";
  const setState = (next: RunnerState) => { phase = next.phase; d.state(next); };
  const notSent = (ids: string[], message: string) => {
    for (const guildId of ids) d.status(guildId, { at: new Date().toISOString(), state: "not_sent", message });
  };
  const isAborted = (signal: AbortSignal) => { if (signal.aborted) throw new AbortedError(); };

  return {
    get busy() { return controller !== null; },
    /** An upload cannot be undone; stop also skips the remaining guilds in that situation. */
    stop() {
      if (!controller) return;
      controller.abort();
      d.notify(phase === "uploading"
        ? "L'envoi est déjà en cours et peut être enregistré. Les serveurs suivants seront ignorés."
        : "Arrêt demandé : cette collecte ne sera pas envoyée.");
    },
    async run(guildIds: string[], { requireTracked = true }: { requireTracked?: boolean } = {}) {
      if (controller) { d.notify("Une collecte ou un envoi est déjà en cours."); return; }
      const ids = [...new Set(guildIds)].filter(id => snowflake(id) && (!requireTracked || d.isTracked(id)));
      if (ids.length === 0) { d.notify(requireTracked ? "Coche au moins un serveur à suivre." : "Serveur Discord invalide : rien envoyé."); return; }
      controller = new AbortController();
      const signal = controller.signal;
      let nextIndex = 0;
      try {
        setState({ busy: true, phase: "collecting", guildId: ids[0] });
        const config = await guarded(d.config(), signal, Infinity);
        isAborted(signal);
        const valid = validatePostArgs({ ...config, guildId: ids[0], body: "{}" });
        if (!valid.ok) { d.notify(valid.error); notSent(ids, valid.error); return; }
        for (let i = 0; i < ids.length; i++) {
          const guildId = ids[i]!;
          nextIndex = i;
          isAborted(signal);
          if (requireTracked && !d.isTracked(guildId)) { notSent([guildId], "Serveur retiré du suivi : non envoyé."); continue; }
          setState({ busy: true, phase: "collecting", guildId, progress: "Démarrage de la collecte…" });
          let outcome: Outcome;
          try {
            const collection = await d.collect(guildId, signal, (method, count) => {
              setState({ busy: true, phase: "collecting", guildId, progress: `${count} membres (${method})` });
            });
            isAborted(signal);
            if (collection.members.length === 0) throw new Error("Aucun membre fraîchement collecté : rien n'a été envoyé.");
            if (requireTracked && !d.isTracked(guildId)) { notSent([guildId], "Serveur retiré du suivi : non envoyé."); continue; }
            if (collection.durationMs > 1_800_000) throw new Error("Collecte trop longue : aucun envoi, recommence.");
            const coverage = computeCoverage(collection.method, collection.members.length, collection.expected, collection.stopReason);
            const payload = buildPayload({
              pluginVersion: "1.0.0", collectedAt: collection.collectedAt, collectionDurationMs: collection.durationMs,
              guild: collection.guild, roles: collection.roles, members: collection.members, coverage,
            });
            if (!payload.ok) outcome = describePayloadRefusal(payload.reason);
            else {
              const currentConfig = await guarded(d.config(), signal, Infinity);
              const currentValid = validatePostArgs({ ...currentConfig, guildId, body: "{}" });
              if (!currentValid.ok || currentValid.value.url !== valid.value.url
                || currentValid.value.fingerprint !== valid.value.fingerprint || currentValid.value.apiKey !== valid.value.apiKey)
                throw new Error("La configuration a changé pendant la collecte : rien n'a été envoyé.");
              // Last gate: no async operation is allowed between this check and invoking IPC.
              setState({ busy: true, phase: "uploading", guildId, progress: "Envoi au tracker…" });
              isAborted(signal);
              if (requireTracked && !d.isTracked(guildId)) { notSent([guildId], "Serveur retiré du suivi : non envoyé."); continue; }
              // IPC cannot be cancelled once invoked. A rejected bridge does not prove the
              // native process never uploaded, so expose the same uncertain outcome as TLS.
              let reply: PostResult;
              try { reply = await d.post({ ...valid.value, guildId, body: payload.json }); }
              catch { reply = { status: 0, body: "", retryAfter: null, error: "no_response_after_upload" }; }
              outcome = describeResult(reply, { method: collection.method, trackerUrl: valid.value.url });
              let dto: Record<string, unknown> = {};
              try { dto = JSON.parse(reply.body); } catch { /* non-JSON nginx response */ }
              d.status(guildId, {
                at: new Date().toISOString(), state: outcome.ok ? "sent" : "failed", message: outcome.message,
                method: collection.method, count: collection.members.length,
                complete: outcome.ok && dto?.isComplete === true,
                orgSid: typeof dto?.orgSid === "string" ? dto.orgSid : null,
              });
            }
            if (!payload.ok) d.status(guildId, { at: new Date().toISOString(), state: "failed", message: outcome.message, method: collection.method, count: collection.members.length });
          } catch (error) {
            if (error instanceof AbortedError || signal.aborted) throw new AbortedError();
            outcome = { ok: false, stopBatch: false, message: error instanceof Error ? error.message : "Collecte Discord impossible : rien envoyé." };
            d.status(guildId, { at: new Date().toISOString(), state: "failed", message: outcome.message });
          }
          d.notify(outcome.message, outcome.ok);
          nextIndex = i + 1;
          if (outcome.stopBatch || signal.aborted) {
            notSent(ids.slice(i + 1), "Lot arrêté : non envoyé.");
            break;
          }
          if (i + 1 < ids.length) {
            setState({ busy: true, phase: "waiting", progress: "Pause entre deux serveurs…" });
            await collectionSleep(d.batchDelayMs ?? 5000, signal);
          }
        }
      } catch (error) {
        const message = signal.aborted || error instanceof AbortedError
          ? "Collecte arrêtée : rien n'a été envoyé pour ces serveurs."
          : error instanceof Error ? error.message : "Impossible de préparer l'envoi.";
        notSent(ids.slice(nextIndex), message);
        d.notify(message, false);
      } finally {
        controller = null;
        setState({ busy: false, phase: "idle" });
      }
    },
  };
}
