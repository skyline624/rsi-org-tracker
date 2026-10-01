import { record } from "./discordProtocol";
import { validatePostArgs } from "./postArgs";
import type { SyncConfig } from "./syncRunner";

/** Bind the locally saved secret to a canonical origin and certificate, never Cloud Sync alone. */
export function bindCredentials(config: SyncConfig): SyncConfig {
  const valid = validatePostArgs({ ...config, guildId: "100000000000000001", body: "{}" });
  if (!valid.ok) throw new Error(valid.error);
  const { url, fingerprint, apiKey } = valid.value;
  return { url, fingerprint, apiKey };
}

export function savedCredentials(value: unknown): SyncConfig | null {
  const v = record(value);
  if (!v || typeof v.url !== "string" || typeof v.fingerprint !== "string" || typeof v.apiKey !== "string") return null;
  try { return bindCredentials({ url: v.url, fingerprint: v.fingerprint, apiKey: v.apiKey }); }
  catch { return null; }
}

/** A settings import or Cloud Sync change cannot redirect the stored key. Legacy keys need resaving. */
export function credentialsForSettings(value: unknown, url: string, fingerprint: string): SyncConfig {
  const saved = savedCredentials(value);
  if (!saved) throw new Error("Enregistre la configuration locale pour lier la clé à ce tracker et à son certificat.");
  let selected: SyncConfig;
  try { selected = bindCredentials({ url, fingerprint, apiKey: saved.apiKey }); }
  catch { throw new Error("La configuration du tracker a changé. Vérifie-la et enregistre-la à nouveau avant l'envoi."); }
  if (saved.url !== selected.url || saved.fingerprint !== selected.fingerprint)
    throw new Error("La configuration du tracker a changé. Vérifie-la et enregistre-la à nouveau avant l'envoi.");
  return saved;
}
