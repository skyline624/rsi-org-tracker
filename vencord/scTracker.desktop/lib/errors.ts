// Type-only imports: the renderer bundle must never pull node:https in (Vencord refuses Node
// built-ins outside native.ts), and `import type` is erased before bundling.
import type { SyncMethod } from "./payload";
import type { PostResult } from "./pinnedPost";

/** What the toast and the settings panel show. `stopBatch`: skip the remaining guilds of a batch. */
export type Outcome = { ok: boolean; message: string; stopBatch: boolean };

/** What the plugin knows beyond the answer: the method it used, and the tracker URL for the /discord link. */
export type DescribeContext = { method?: SyncMethod; trackerUrl?: string };

const TOO_LARGE = "Serveur trop grand pour un envoi";
const UNREACHABLE = "Tracker injoignable";
const MAX_DETAIL_LENGTH = 300;

const stop = (message: string): Outcome => ({ ok: false, message, stopBatch: true });
const next = (message: string): Outcome => ({ ok: false, message, stopBatch: false });

/** The body as a JSON object, or null for HTML (nginx), an empty body or any other JSON value. */
function jsonObject(body: unknown): Record<string, unknown> | null {
  if (typeof body !== "string" || body === "") return null;
  try {
    const value: unknown = JSON.parse(body);
    return typeof value === "object" && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

const count = (value: unknown): number => (typeof value === "number" && Number.isFinite(value) ? value : 0);
const plural = (n: number, one: string, many: string) => `${n} ${n > 1 ? many : one}`;
const delay = (seconds: number | null | undefined) =>
  (typeof seconds === "number" && Number.isFinite(seconds) ? `${seconds} s` : "quelques minutes");

/** The 200 summary, from the API's DiscordSyncResponseDto. */
function describeSuccess(body: unknown, ctx: DescribeContext): Outcome {
  const dto = jsonObject(body);
  if (dto === null) return { ok: true, message: "Envoi accepté par le tracker.", stopBatch: false };

  const events = (typeof dto.events === "object" && dto.events !== null ? dto.events : {}) as Record<string, unknown>;
  const eventParts = [
    [count(events.joined), "arrivée", "arrivées"],
    [count(events.left), "départ", "départs"],
    [count(events.rejoined), "retour", "retours"],
    [count(events.rolesChanged), "changement de rôles", "changements de rôles"],
    [count(events.nickChanged), "changement de pseudo", "changements de pseudo"],
    [count(events.nameChanged), "changement de nom", "changements de nom"],
  ] as const;
  const listed = eventParts.filter(([n]) => n > 0).map(([n, one, many]) => plural(n, one, many));

  const sentences = [
    `${dto.isComplete === true ? "Envoi complet" : "Envoi partiel"} : ${plural(count(dto.membersReceived), "membre", "membres")}`
      + `${ctx.method ? ` (${ctx.method})` : ""}.`,
  ];
  if (dto.isBaseline === true) sentences.push("Premier envoi : état de référence enregistré.");
  sentences.push(listed.length === 0 ? "Aucun événement." : `Événements : ${listed.join(", ")}.`);
  const optedOut = count(dto.membersOptedOut);
  if (optedOut > 0) sentences.push(`${plural(optedOut, "compte exclu du suivi ignoré", "comptes exclus du suivi ignorés")}.`);
  if (dto.massDepartureDetected === true) {
    sentences.push("Départs massifs constatés : tous les départs ont été enregistrés dans l'historique.");
  } else if (dto.departureGuardTripped === true) {
    sentences.push("Cet ancien tracker a bloqué les départs. Mets le tracker à jour pour les enregistrer automatiquement.");
  }
  if (typeof dto.orgSid === "string" && dto.orgSid !== "") {
    sentences.push(`Corpo : ${dto.orgSid}.`);
  } else {
    const site = ctx.trackerUrl ? `${ctx.trackerUrl.replace(/\/+$/, "")}/discord` : "le site, onglet DISCORD";
    sentences.push(`Serveur non relié à une corpo : relie-le sur ${site}.`);
  }
  return { ok: true, message: sentences.join(" "), stopBatch: false };
}

/**
 * Translates what native.ts returned into a French message (spec § 14.1) and says whether a
 * batch goes on (spec § 5.3). It stops on 401, 403, 429, 503, guild_excluded and a certificate
 * mismatch, and also when the tracker is unreachable (network, no answer, nginx 502/504): the
 * next guilds would each cost a full collection for the same failure. It continues after 400,
 * 413, 409 stale_sync, 500 and other statuses, which concern this guild's sync. Reads the
 * ProblemDetails `code` and `detail` when the body is JSON; never throws.
 */
export function describeResult(r: PostResult, ctx: DescribeContext = {}): Outcome {
  try {
    if (r?.error === "pin_mismatch") {
      return stop("Certificat inattendu : vérifie l'empreinte (voir Paramètres → Clé d'envoi Discord sur le site). Rien n'a été envoyé.");
    }
    if (r?.error === "no_response_after_upload") {
      return stop("Pas de réponse du tracker : l'envoi a peut-être été enregistré. Un renvoi ne crée aucun doublon d'événement");
    }
    const status = r?.status;
    if (r?.error === "network" || typeof status !== "number" || status === 0) return stop(UNREACHABLE);
    if (status >= 200 && status < 300) return describeSuccess(r.body, ctx);

    const problem = jsonObject(r.body);
    const code = typeof problem?.code === "string" ? problem.code : null;
    const detail = typeof problem?.detail === "string" ? problem.detail.trim() : "";

    switch (status) {
      case 400:
        return next(detail === ""
          ? "Données refusées"
          : `Données refusées : ${detail.length > MAX_DETAIL_LENGTH ? `${detail.slice(0, MAX_DETAIL_LENGTH)}…` : detail}`);
      case 401:
        return stop("Clé invalide, révoquée ou expirée");
      case 403:
        return stop("Accès refusé");
      case 409:
        if (code === "stale_sync") return next("Un envoi plus récent a été reçu pendant ta collecte");
        if (code === "guild_excluded") return stop("Ce serveur est exclu du suivi");
        break;
      case 413:
        return next(TOO_LARGE);
      case 429:
        return stop(`Trop d'envois, réessaie dans ${delay(r.retryAfter)}`);
      case 503:
        return stop(`Tracker occupé, réessaie dans ${delay(r.retryAfter)}`);
      case 502:
      case 504:
        return stop(`Erreur du tracker (HTTP ${status}), réessaie plus tard`);
    }
    return next(`Erreur du tracker (HTTP ${status}), réessaie plus tard`);
  } catch {
    return next("Erreur du tracker, réessaie plus tard");
  }
}

/** The message for a body refused before sending (buildPayload), the same as the tracker's 413. */
export function describePayloadRefusal(reason: "too_many_members" | "too_large"): Outcome {
  return next(reason === "too_many_members" ? `${TOO_LARGE} (plus de 50 000 membres)` : TOO_LARGE);
}
