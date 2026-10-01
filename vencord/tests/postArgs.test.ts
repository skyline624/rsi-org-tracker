import { describe, expect, it } from "vitest";

import { MAX_BODY_BYTES } from "../scTracker.desktop/lib/payload";
import { validatePostArgs } from "../scTracker.desktop/lib/postArgs";

const COLONS = "0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0:0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0";
const HEX = COLONS.replaceAll(":", "");
const GUILD_ID = "123456789012345678";

const valid = {
  url: "https://203.0.113.10",
  fingerprint: `sha256 Fingerprint=${COLONS}`,
  apiKey: "sct_0123456789abcdef",
  guildId: GUILD_ID,
  body: '{"pluginVersion":"1.0.0"}',
};

function refused(args: unknown): string {
  let result: ReturnType<typeof validatePostArgs> | undefined;
  expect(() => { result = validatePostArgs(args); }).not.toThrow();
  if (!result || result.ok) throw new Error(`expected a refusal for ${JSON.stringify(args)}`);
  return result.error;
}

describe("validatePostArgs", () => {
  it("returns the normalised arguments and the ingest path", () => {
    expect(validatePostArgs(valid)).toEqual({
      ok: true,
      value: { url: "https://203.0.113.10", fingerprint: HEX, apiKey: "sct_0123456789abcdef", guildId: GUILD_ID, body: valid.body },
      path: `/ingest/discord/guilds/${GUILD_ID}/syncs`,
    });
  });

  it.each([
    ["a trailing slash", "https://1.2.3.4/", "https://1.2.3.4"],
    ["the default port", "https://1.2.3.4:443", "https://1.2.3.4"],
    ["the default port and a slash", "https://1.2.3.4:443/", "https://1.2.3.4"],
    ["an upper-case scheme and host", "HTTPS://Host", "https://host"],
    ["surrounding blanks", "  https://1.2.3.4\n", "https://1.2.3.4"],
    ["another port", "https://1.2.3.4:8443/", "https://1.2.3.4:8443"],
    ["an IPv6 address", "https://[2001:db8::1]:8443", "https://[2001:db8::1]:8443"],
  ])("normalises a URL with %s", (_label, url, expected) => {
    const result = validatePostArgs({ ...valid, url });

    expect(result.ok && result.value.url).toBe(expected);
  });

  it.each([
    ["http", "http://1.2.3.4", "doit commencer par https://"],
    ["another scheme", "ftp://1.2.3.4", "doit commencer par https://"],
    ["a path", "https://1.2.3.4/ingest/discord", "ni chemin ni paramètres"],
    ["a double slash", "https://1.2.3.4//", "ni chemin ni paramètres"],
    ["a query", "https://1.2.3.4/?x=1", "ni chemin ni paramètres"],
    ["an empty query", "https://1.2.3.4?", "ni chemin ni paramètres"],
    ["a fragment", "https://1.2.3.4/#top", "ni chemin ni paramètres"],
    ["credentials", "https://user:secret@1.2.3.4", "identifiants"],
    ["no scheme", "1.2.3.4", "URL du tracker invalide"],
    ["an empty value", "", "URL du tracker invalide"],
    ["a non-string", 42, "URL du tracker invalide"],
  ])("refuses a URL with %s", (_label, url, message) => {
    expect(refused({ ...valid, url })).toContain(message);
  });

  it("normalises the fingerprint and refuses one that is not a SHA-256", () => {
    const result = validatePostArgs({ ...valid, fingerprint: COLONS.toLowerCase() });

    expect(result.ok && result.value.fingerprint).toBe(HEX);
    expect(refused({ ...valid, fingerprint: HEX.slice(1) })).toContain("Empreinte");
    expect(refused({ ...valid, fingerprint: undefined })).toContain("Empreinte");
  });

  it.each([["1234567890123456"], ["123456789012345678901"], ["12345678901234567a"], [123456789], [""]])(
    "refuses the guild id %j", guildId => {
      expect(refused({ ...valid, guildId })).toContain("Identifiant de serveur");
    });

  it("trims the key and refuses a missing, oversized or multi-line one", () => {
    const result = validatePostArgs({ ...valid, apiKey: "  sct_key\n" });

    expect(result.ok && result.value.apiKey).toBe("sct_key");
    expect(refused({ ...valid, apiKey: "   " })).toContain("Clé d'API manquante");
    expect(refused({ ...valid, apiKey: undefined })).toContain("Clé d'API manquante");
    expect(refused({ ...valid, apiKey: "k".repeat(201) })).toContain("Clé d'API invalide");
    expect(refused({ ...valid, apiKey: "sct_a\r\nx-evil: 1" })).toContain("Clé d'API invalide");
    expect(validatePostArgs({ ...valid, apiKey: "k".repeat(200) }).ok).toBe(true);
  });

  it("refuses an empty body and a body over MAX_BODY_BYTES bytes", () => {
    expect(refused({ ...valid, body: "" })).toContain("rien à envoyer");
    expect(refused({ ...valid, body: null })).toContain("rien à envoyer");
    expect(refused({ ...valid, body: "é".repeat(MAX_BODY_BYTES / 2 + 1) })).toBe("Serveur trop grand pour un envoi");
    expect(validatePostArgs({ ...valid, body: "a".repeat(MAX_BODY_BYTES) }).ok).toBe(true);
  });

  it.each([[null], [undefined], ["https://1.2.3.4"], [[valid]]])("refuses %j as arguments", args => {
    expect(refused(args)).toBe("Arguments d'envoi invalides");
  });
});
