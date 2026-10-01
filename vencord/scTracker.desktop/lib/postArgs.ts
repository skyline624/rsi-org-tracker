import { normalizeFingerprint } from "./fingerprint";
import { MAX_BODY_BYTES, utf8ByteLength } from "./payload";

export type PostSyncArgs = { url: string; fingerprint: string; apiKey: string; guildId: string; body: string };

type Refusal = { ok: false; error: string };

const MAX_API_KEY_LENGTH = 200;
const SNOWFLAKE = /^[0-9]{17,20}$/;
/** Visible ASCII only: anything else would be refused as an HTTP header value, or split it. */
const HEADER_SAFE = /^[\x21-\x7e]+$/;

const refuse = (error: string): Refusal => ({ ok: false, error });

/**
 * Normalises the tracker URL to "https://host[:port]". Harmless variants (case, blanks, a
 * trailing slash, the default port 443) are accepted; anything that would send the key
 * elsewhere than the tracker's root (another scheme, credentials, a path, a query, a fragment)
 * is refused.
 */
function normalizeTrackerUrl(input: unknown): { ok: true; url: string } | Refusal {
  const invalid = "URL du tracker invalide : saisis https://<adresse du tracker>, affichée sur le site (Paramètres → Clé d'envoi Discord).";
  if (typeof input !== "string" || input.trim() === "") return refuse(invalid);
  const raw = input.trim();
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return refuse(invalid);
  }
  if (url.protocol !== "https:") return refuse("L'URL du tracker doit commencer par https://.");
  if (url.username !== "" || url.password !== "") return refuse("L'URL du tracker ne doit pas contenir d'identifiants.");
  if (url.pathname !== "/" || url.search !== "" || url.hash !== "" || raw.includes("?") || raw.includes("#")) {
    return refuse("L'URL du tracker ne doit contenir ni chemin ni paramètres : https://<adresse du tracker> seulement.");
  }
  return { ok: true, url: `https://${url.host}` };
}

/**
 * Checks what the renderer sends to native.ts before anything leaves the machine, and builds
 * the ingest path itself so the renderer cannot choose where the key goes. Never throws: every
 * refusal is a French message for the toast.
 */
export function validatePostArgs(a: unknown): { ok: true; value: PostSyncArgs; path: string } | Refusal {
  if (typeof a !== "object" || a === null || Array.isArray(a)) return refuse("Arguments d'envoi invalides");
  const args = a as Record<string, unknown>;

  const url = normalizeTrackerUrl(args.url);
  if (!url.ok) return url;

  const fingerprint = typeof args.fingerprint === "string" ? normalizeFingerprint(args.fingerprint) : null;
  if (fingerprint === null) {
    return refuse("Empreinte du certificat invalide : colle celle affichée sur le site (64 caractères hexadécimaux).");
  }

  const guildId = args.guildId;
  if (typeof guildId !== "string" || !SNOWFLAKE.test(guildId)) return refuse("Identifiant de serveur Discord invalide.");

  const apiKey = typeof args.apiKey === "string" ? args.apiKey.trim() : "";
  if (apiKey === "") return refuse("Clé d'API manquante : colle-la dans les réglages du plugin.");
  if (apiKey.length > MAX_API_KEY_LENGTH || !HEADER_SAFE.test(apiKey)) {
    return refuse("Clé d'API invalide : recopie-la depuis le site.");
  }

  const body = args.body;
  if (typeof body !== "string" || body === "") return refuse("Message vide : rien à envoyer.");
  if (utf8ByteLength(body) > MAX_BODY_BYTES) return refuse("Serveur trop grand pour un envoi");

  return {
    ok: true,
    value: { url: url.url, fingerprint, apiKey, guildId, body },
    path: `/ingest/discord/guilds/${guildId}/syncs`,
  };
}
