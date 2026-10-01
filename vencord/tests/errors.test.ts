import { describe, expect, it } from "vitest";

import { describePayloadRefusal, describeResult } from "../scTracker.desktop/lib/errors";
import type { PostResult } from "../scTracker.desktop/lib/pinnedPost";

const NGINX_HTML = (code: number, title: string) =>
  `<html>\r\n<head><title>${code} ${title}</title></head>\r\n<body>\r\n<center><h1>${code} ${title}</h1></center>\r\n<hr><center>nginx</center>\r\n</body>\r\n</html>\r\n`;

function answer(status: number, body: unknown = "", retryAfter: number | null = null): PostResult {
  return { status, body: typeof body === "string" ? body : JSON.stringify(body), retryAfter };
}

function problem(status: number, extra: Record<string, unknown> = {}) {
  return { type: "about:blank", title: "Error", status, instance: "/api/ingest/discord/guilds/1/syncs", correlationId: "abc", ...extra };
}

const SYNC = {
  syncId: 42, isBaseline: false, isComplete: true, departureGuardTripped: false, orgSid: "CORP",
  membersReceived: 1234, membersOptedOut: 0, unknownRoleRefs: 0,
  events: { joined: 3, left: 1, rejoined: 0, rolesChanged: 5, nickChanged: 2, nameChanged: 0 },
};

describe("describeResult: transport failures", () => {
  it("pin_mismatch stops the batch and says nothing was sent", () => {
    expect(describeResult({ status: 0, body: "", retryAfter: null, error: "pin_mismatch" })).toEqual({
      ok: false,
      message: "Certificat inattendu : vérifie l'empreinte (voir Paramètres → Clé d'envoi Discord sur le site). Rien n'a été envoyé.",
      stopBatch: true,
    });
  });

  it("network stops the batch", () => {
    expect(describeResult({ status: 0, body: "", retryAfter: null, error: "network" }))
      .toEqual({ ok: false, message: "Tracker injoignable", stopBatch: true });
    expect(describeResult({ status: 0, body: "", retryAfter: null }))
      .toEqual({ ok: false, message: "Tracker injoignable", stopBatch: true });
  });

  it("no_response_after_upload says the sync may have been recorded", () => {
    expect(describeResult({ status: 0, body: "", retryAfter: null, error: "no_response_after_upload" })).toEqual({
      ok: false,
      message: "Pas de réponse du tracker : l'envoi a peut-être été enregistré. Un renvoi ne crée aucun doublon d'événement",
      stopBatch: true,
    });
  });
});

describe("describeResult: tracker refusals", () => {
  it.each([
    [401, "Clé invalide, révoquée ou expirée"],
    [403, "Accès refusé"],
  ])("%i stops the batch", (status, message) => {
    expect(describeResult(answer(status, problem(status)))).toEqual({ ok: false, message, stopBatch: true });
    expect(describeResult(answer(status, ""))).toEqual({ ok: false, message, stopBatch: true });
  });

  it("400 adds the ProblemDetails detail and lets the batch continue", () => {
    const r = answer(400, problem(400, { detail: "members[3].userId : snowflake attendu.", code: "invalid_sync" }));

    expect(describeResult(r))
      .toEqual({ ok: false, message: "Données refusées : members[3].userId : snowflake attendu.", stopBatch: false });
  });

  it("400 without a readable detail still gives a message", () => {
    expect(describeResult(answer(400, NGINX_HTML(400, "Bad Request"))))
      .toEqual({ ok: false, message: "Données refusées", stopBatch: false });
    expect(describeResult(answer(400, problem(400, { detail: "   " }))).message).toBe("Données refusées");
  });

  it("400 shortens a very long detail", () => {
    const message = describeResult(answer(400, problem(400, { detail: "x".repeat(1000) }))).message;

    expect(message.length).toBeLessThanOrEqual("Données refusées : ".length + 301);
    expect(message.endsWith("…")).toBe(true);
  });

  it("409 stale_sync lets the batch continue", () => {
    expect(describeResult(answer(409, problem(409, { code: "stale_sync", detail: "…" })))).toEqual({
      ok: false, message: "Un envoi plus récent a été reçu pendant ta collecte", stopBatch: false,
    });
  });

  it("409 guild_excluded stops the batch", () => {
    expect(describeResult(answer(409, problem(409, { code: "guild_excluded" })))).toEqual({
      ok: false, message: "Ce serveur est exclu du suivi", stopBatch: true,
    });
  });

  it("409 without a known code is an unexpected error", () => {
    expect(describeResult(answer(409, problem(409)))).toEqual({
      ok: false, message: "Erreur du tracker (HTTP 409), réessaie plus tard", stopBatch: false,
    });
  });

  it.each([
    ["the API's ProblemDetails", JSON.stringify(problem(413, { title: "Payload Too Large" }))],
    ["nginx's HTML page", NGINX_HTML(413, "Request Entity Too Large")],
  ])("413 from %s lets the batch continue", (_label, body) => {
    expect(describeResult(answer(413, body)))
      .toEqual({ ok: false, message: "Serveur trop grand pour un envoi", stopBatch: false });
  });

  it("429 with Retry-After gives the delay and stops the batch", () => {
    expect(describeResult(answer(429, problem(429), 120)))
      .toEqual({ ok: false, message: "Trop d'envois, réessaie dans 120 s", stopBatch: true });
  });

  it("429 from nginx, without Retry-After, asks to retry in a few minutes", () => {
    expect(describeResult(answer(429, NGINX_HTML(429, "Too Many Requests"))))
      .toEqual({ ok: false, message: "Trop d'envois, réessaie dans quelques minutes", stopBatch: true });
  });

  it("503 gives the delay and stops the batch", () => {
    expect(describeResult(answer(503, problem(503, { code: "busy" }), 30)))
      .toEqual({ ok: false, message: "Tracker occupé, réessaie dans 30 s", stopBatch: true });
    expect(describeResult(answer(503, "")))
      .toEqual({ ok: false, message: "Tracker occupé, réessaie dans quelques minutes", stopBatch: true });
  });

  it("500 and unexpected statuses let the batch continue", () => {
    expect(describeResult(answer(500, problem(500))))
      .toEqual({ ok: false, message: "Erreur du tracker (HTTP 500), réessaie plus tard", stopBatch: false });
    expect(describeResult(answer(418, "")))
      .toEqual({ ok: false, message: "Erreur du tracker (HTTP 418), réessaie plus tard", stopBatch: false });
  });

  it.each([502, 504])("%i from nginx (API down) stops the batch", status => {
    expect(describeResult(answer(status, NGINX_HTML(status, "Bad Gateway")))).toEqual({
      ok: false, message: `Erreur du tracker (HTTP ${status}), réessaie plus tard`, stopBatch: true,
    });
  });

  it.each([[""], ["null"], ["[]"], ["42"], ['"text"'], ["{"], ['{"code":42,"detail":["x"]}']])(
    "never throws on the body %j", body => {
      for (const status of [200, 400, 409, 413, 429, 500]) {
        expect(() => describeResult(answer(status, body))).not.toThrow();
        expect(describeResult(answer(status, body)).message.length).toBeGreaterThan(0);
      }
    });

  it("never throws on a malformed result", () => {
    expect(describeResult(undefined as unknown as PostResult))
      .toEqual({ ok: false, message: "Tracker injoignable", stopBatch: true });
    expect(describeResult({ status: 200 } as unknown as PostResult).ok).toBe(true);
  });
});

describe("describeResult: success", () => {
  it("summarises the sync from DiscordSyncResponseDto", () => {
    expect(describeResult(answer(200, SYNC), { method: "member-search", trackerUrl: "https://1.2.3.4" })).toEqual({
      ok: true,
      message: "Envoi complet : 1234 membres (member-search). "
        + "Événements : 3 arrivées, 1 départ, 5 changements de rôles, 2 changements de pseudo. Corpo : CORP.",
      stopBatch: false,
    });
  });

  it("points to the site when the guild is not linked to a corpo", () => {
    const unlinked = { ...SYNC, orgSid: null };

    expect(describeResult(answer(200, unlinked), { trackerUrl: "https://1.2.3.4/" }).message)
      .toContain("Serveur non relié à une corpo : relie-le sur https://1.2.3.4/discord.");
    expect(describeResult(answer(200, unlinked)).message)
      .toContain("Serveur non relié à une corpo : relie-le sur le site, onglet DISCORD.");
  });

  it("reports recorded mass departures without requesting approval", () => {
    const mass = { ...SYNC, massDepartureDetected: true, events: { ...SYNC.events, left: 12 } };
    const message = describeResult(answer(200, mass)).message;

    expect(message).toContain("Envoi complet");
    expect(message).toContain("12 départs");
    expect(message).toContain("Départs massifs constatés : tous les départs ont été enregistrés dans l'historique.");
    expect(message).not.toContain("autoriser");
  });

  it("explains a legacy tracker blocking departures", () => {
    const guarded = { ...SYNC, isComplete: false, departureGuardTripped: true };

    const message = describeResult(answer(200, guarded)).message;

    expect(message).toContain("Envoi partiel");
    expect(message).toContain("Cet ancien tracker a bloqué les départs.");
  });

  it("mentions a baseline, opted-out accounts, a single member and no event", () => {
    const baseline = {
      ...SYNC, isBaseline: true, isComplete: false, membersReceived: 1, membersOptedOut: 2,
      events: { joined: 0, left: 0, rejoined: 0, rolesChanged: 0, nickChanged: 0, nameChanged: 0 },
    };

    expect(describeResult(answer(200, baseline), { method: "cache" }).message).toBe(
      "Envoi partiel : 1 membre (cache). Premier envoi : état de référence enregistré. Aucun événement. "
      + "2 comptes exclus du suivi ignorés. Corpo : CORP.");
  });

  it("lists every kind of event with French plurals", () => {
    const all = { ...SYNC, events: { joined: 1, left: 2, rejoined: 1, rolesChanged: 1, nickChanged: 1, nameChanged: 3 } };

    expect(describeResult(answer(200, all)).message).toContain(
      "Événements : 1 arrivée, 2 départs, 1 retour, 1 changement de rôles, 1 changement de pseudo, 3 changements de nom.");
  });

  it("accepts a 2xx answer whose body is not the expected JSON", () => {
    expect(describeResult(answer(200, "OK"))).toEqual({ ok: true, message: "Envoi accepté par le tracker.", stopBatch: false });
  });
});

describe("describePayloadRefusal", () => {
  it("uses the 413 message before anything is sent", () => {
    expect(describePayloadRefusal("too_large"))
      .toEqual({ ok: false, message: "Serveur trop grand pour un envoi", stopBatch: false });
    expect(describePayloadRefusal("too_many_members"))
      .toEqual({ ok: false, message: "Serveur trop grand pour un envoi (plus de 50 000 membres)", stopBatch: false });
  });
});
