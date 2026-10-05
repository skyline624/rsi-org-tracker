import { describe, expect, it } from "vitest";
import type { DiscordReconciliation } from "@/lib/api/types";
import {
  cleanDiscordText,
  confidenceBadge,
  detectedOrgLabel,
  discordDisplayName,
  discrepancyKindBadge,
  eventTone,
  eventTypeLabel,
  eventWhen,
  formatShare,
  formatUtc,
  guildIconUrl,
  parseRoleList,
  rankChangeText,
  reconciliationBadge,
  rolesDiff,
  safeRoleColor,
  strongSource,
  syncBadges,
  timelineSourceBadge,
  timelineTypeLabel,
  timelineWhen,
} from "./format";

const GUILD = "123456789012345678";
const HASH = "0123456789abcdef0123456789abcdef";

describe("cleanDiscordText", () => {
  it.each([
    "<script>alert(1)</script>",
    "<img src=x onerror=alert(1)>",
    "**gras** _italique_ [lien](https://evil.example) `code`",
    "@everyone",
  ])("keeps %s as literal text, for React to escape", (name) => {
    expect(cleanDiscordText(name)).toBe(name);
  });

  it("removes the bidi overrides and isolates that would reorder the row", () => {
    expect(cleanDiscordText("\u202Eexe.txt")).toBe("exe.txt");
    expect(cleanDiscordText("a\u2067b\u2069c\u200Fd")).toBe("abcd");
  });

  it("removes zero-width spaces, word joiners and byte order marks", () => {
    expect(cleanDiscordText("Pi\u200Blo\u2060te\uFEFF")).toBe("Pilote");
  });

  it("keeps the zero-width joiners of emoji sequences", () => {
    expect(cleanDiscordText("👩\u200D🚀 Pilote")).toBe("👩\u200D🚀 Pilote");
  });

  it.each([null, undefined, "", "   ", "\u200B\u200D", "\u202E\u200B "])("has nothing to show for %j", (value) => {
    expect(cleanDiscordText(value)).toBeNull();
  });

  it("never shortens a long name: cutting it is left to CSS", () => {
    const long = "Ｗ".repeat(32) + "x".repeat(300);

    expect(cleanDiscordText(long)).toBe(long);
  });
});

describe("discordDisplayName", () => {
  const account = {
    discordUserId: "323456789012345678",
    username: "pilote42",
    globalName: "Pilote",
    nick: "[CORP] Pilote42",
  };

  it("prefers the server nick, then the global name, then the username", () => {
    expect(discordDisplayName(account)).toBe("[CORP] Pilote42");
    expect(discordDisplayName({ ...account, nick: null })).toBe("Pilote");
    expect(discordDisplayName({ ...account, nick: null, globalName: null })).toBe("pilote42");
  });

  it("skips a name with nothing visible in it", () => {
    expect(discordDisplayName({ ...account, nick: "\u200B\u200B" })).toBe("Pilote");
  });

  it("falls back to the account id", () => {
    expect(discordDisplayName({ ...account, nick: null, globalName: null, username: "\u200B" })).toBe(
      "323456789012345678",
    );
  });
});

describe("guildIconUrl", () => {
  it("builds the CDN address from a valid id and hash", () => {
    expect(guildIconUrl(GUILD, HASH)).toBe(`https://cdn.discordapp.com/icons/${GUILD}/${HASH}.png?size=64`);
    expect(guildIconUrl(GUILD, `a_${HASH}`)).toBe(`https://cdn.discordapp.com/icons/${GUILD}/a_${HASH}.png?size=64`);
  });

  it.each([
    [GUILD, null],
    ["../../evil", HASH],
    ["123", HASH],
    [GUILD, "../../../attachments/x"],
    [GUILD, `${HASH}?x=1`],
    [GUILD, HASH.toUpperCase()],
    [GUILD, "javascript:alert(1)"],
  ])("builds no address for server %j and icon %j", (guildId, iconHash) => {
    expect(guildIconUrl(guildId, iconHash)).toBeNull();
  });
});

describe("safeRoleColor", () => {
  it("keeps a #rrggbb colour", () => {
    expect(safeRoleColor("#e67e22")).toBe("#e67e22");
  });

  it.each([null, "#000000", "red", "#fff", "#e67e22;background:url(x)", "expression(alert(1))", "#E67E2G"])(
    "drops %j",
    (color) => {
      expect(safeRoleColor(color)).toBeNull();
    },
  );
});

describe("syncBadges", () => {
  it("marks a complete upload", () => {
    expect(syncBadges({ isComplete: true, massDepartureDetected: false })).toEqual([
      { label: "COMPLET", tone: "green" },
    ]);
  });

  it("marks a partial upload", () => {
    expect(syncBadges({ isComplete: false, massDepartureDetected: false })).toEqual([
      { label: "PARTIEL", tone: "orange" },
    ]);
  });

  it("signals mass departures while keeping the complete upload", () => {
    expect(syncBadges({ isComplete: true, massDepartureDetected: true })).toEqual([
      { label: "COMPLET", tone: "green" },
      { label: "DÉPARTS MASSIFS", tone: "orange" },
    ]);
  });

  it("adds the baseline", () => {
    expect(syncBadges({ isComplete: true, massDepartureDetected: false, isBaseline: true })).toEqual([
      { label: "COMPLET", tone: "green" },
      { label: "BASE", tone: "cyan" },
    ]);
  });
});

describe("formatUtc", () => {
  it.each([
    ["2025-03-14T20:11:05Z", "2025-03-14 20:11 UTC"],
    ["2025-03-14T20:11:05.123+02:00", "2025-03-14 18:11 UTC"],
    ["2026-09-30T12:05:00", "2026-09-30 12:05 UTC"],
    ["2026-09-30", "2026-09-30 00:00 UTC"],
  ])("%s → %s, whatever the machine's time zone", (iso, text) => {
    expect(formatUtc(iso)).toBe(text);
  });

  it.each([null, undefined, "not a date"])("shows a dash for %j", (iso) => {
    expect(formatUtc(iso)).toBe("—");
  });
});


describe("reconciliationBadge", () => {
  it.each([
    ["ok", "OK", "green"],
    ["unlinked", "non lié", "dim"],
    ["rank_mismatch", "rang différent", "orange"],
    ["not_in_rsi_org", "absent de l'org RSI ou caché sur RSI", "red"],
    ["rsi_unknown", "roster RSI jamais lu", "dim"],
  ] as const)("labels %s in French", (status, label, tone) => {
    expect(reconciliationBadge(status)).toEqual({ label, tone });
  });

  it("has no badge for a bot or an unmapped server", () => {
    expect(reconciliationBadge(null)).toBeNull();
  });

  it("shows a status it does not know as it is", () => {
    expect(reconciliationBadge("new_status" as DiscordReconciliation)).toEqual({ label: "new_status", tone: "dim" });
  });
});

describe("eventTypeLabel", () => {
  it.each([
    ["joined", "arrivée"],
    ["left", "départ"],
    ["rejoined", "retour"],
    ["roles_changed", "rôles"],
    ["nick_changed", "pseudo"],
    ["username_changed", "nom d'utilisateur"],
    ["global_name_changed", "nom affiché"],
  ])("labels %s as « %s »", (type, label) => {
    expect(eventTypeLabel(type)).toBe(label);
  });

  it("spells out an unknown type, Object.prototype names included", () => {
    expect(eventTypeLabel("boost_changed")).toBe("boost changed");
    expect(eventTypeLabel("constructor")).toBe("constructor");
  });
});

describe("eventTone", () => {
  it.each([
    ["joined", "green"],
    ["member_joined", "green"],
    ["left", "red"],
    ["member_left", "red"],
    ["rejoined", "orange"],
    ["roles_changed", "orange"],
    ["nick_changed", "orange"],
  ])("%s is %s", (type, tone) => {
    expect(eventTone(type)).toBe(tone);
  });
});

describe("eventWhen", () => {
  const observedAt = "2026-09-30T12:00:00Z";

  it("gives the exact date when it is known", () => {
    expect(eventWhen({ occurredAt: "2026-09-29T08:30:00Z", notBefore: "2026-09-01T00:00:00Z", observedAt })).toBe(
      "2026-09-29 08:30 UTC",
    );
  });

  it("gives the window between the previous upload and this one otherwise", () => {
    expect(eventWhen({ occurredAt: null, notBefore: "2026-09-28T20:00:00Z", observedAt })).toBe(
      "entre 2026-09-28 20:00 UTC et 2026-09-30 12:00 UTC",
    );
  });

  it("gives the upload date as an upper bound without a previous upload", () => {
    expect(eventWhen({ occurredAt: null, notBefore: null, observedAt })).toBe("au plus tard 2026-09-30 12:00 UTC");
  });
});

describe("rankChangeText", () => {
  it.each([
    [{ from: "Recrue", to: "Officier" }, "Rang : Recrue → Officier"],
    [{ from: null, to: "Officier" }, "Rang : aucun → Officier"],
    [{ from: "\u202Eerucer", to: null }, "Rang : erucer → aucun"],
  ])("%j → %s", (change, text) => {
    expect(rankChangeText(change)).toBe(text);
  });
});

describe("parseRoleList", () => {
  it("reads the roles of a roles_changed value, with their names of the time", () => {
    expect(parseRoleList('[{"id":"1","name":"Officier"},{"id":"2","name":"<b>Pilote</b>"}]')).toEqual([
      { id: "1", name: "Officier" },
      { id: "2", name: "<b>Pilote</b>" },
    ]);
  });

  it.each([null, "", "not json", '{"id":"1"}', "[1,2]"])("reads nothing from %j", (json) => {
    expect(parseRoleList(json)).toEqual([]);
  });

  it("drops malformed entries and extra fields", () => {
    expect(parseRoleList('[{"id":"1","name":"A","color":"#fff"},{"id":2,"name":"B"},{"name":"C"},null]')).toEqual([
      { id: "1", name: "A" },
    ]);
  });
});

describe("rolesDiff", () => {
  it("compares roles by id", () => {
    const before = '[{"id":"1","name":"Recrue"},{"id":"2","name":"Pilote"}]';
    const after = '[{"id":"2","name":"Pilote renommé"},{"id":"3","name":"Officier"}]';

    expect(rolesDiff(before, after)).toEqual({
      added: [{ id: "3", name: "Officier" }],
      removed: [{ id: "1", name: "Recrue" }],
    });
  });
});

describe("discrepancyKindBadge", () => {
  it.each([
    ["rsi_only", "sur RSI seulement", "orange"],
    ["not_in_rsi_org", "absent de l'org RSI ou caché sur RSI", "red"],
    ["rank_mismatch", "rang différent", "orange"],
  ] as const)("labels %s in French", (kind, label, tone) => {
    expect(discrepancyKindBadge(kind)).toEqual({ label, tone });
  });
});

describe("confidenceBadge", () => {
  it.each([
    ["strong", "confiance forte", "green"],
    ["medium", "confiance moyenne", "orange"],
  ] as const)("labels %s in French", (confidence, label, tone) => {
    expect(confidenceBadge(confidence)).toEqual({ label, tone });
  });
});

describe("strongSource", () => {
  it.each([
    ["strong", "tag", "ABC", "via le tag [ABC]"],
    ["strong", "server", "XYZ", "via la corpo du serveur"],
    ["medium", null, null, null],
  ] as const)("says what made a %s suggestion strong (%s)", (confidence, strongVia, strongOrgSid, expected) => {
    expect(strongSource({ confidence, strongVia, strongOrgSid })).toBe(expected);
  });
});

describe("detectedOrgLabel", () => {
  it("names the corpo with its SID and how many tagged members carry it", () => {
    expect(detectedOrgLabel({ sid: "ABC", name: "Corpo ABC", members: 42, taggedMembers: 67 }))
      .toBe("Corpo détectée : Corpo ABC [ABC] — 42 membres sur 67 tagués");
  });

  it("falls back to the SID when the name is blank", () => {
    expect(detectedOrgLabel({ sid: "ABC", name: "  ", members: 3, taggedMembers: 3 }))
      .toBe("Corpo détectée : ABC [ABC] — 3 membres sur 3 tagués");
  });
});

describe("formatShare", () => {
  it.each([
    [3, 4, "75 %"],
    [1, 3, "33 %"],
    [0, 10, "0 %"],
    [0, 0, "—"],
  ])("%d of %d → %s", (part, whole, text) => {
    expect(formatShare(part, whole)).toBe(text);
  });
});


describe("timelineSourceBadge", () => {
  it.each([
    ["rsi", "RSI", "cyan"],
    ["discord", "DISCORD", "dim"],
  ] as const)("marks a %s entry", (source, label, tone) => {
    expect(timelineSourceBadge(source)).toEqual({ label, tone });
  });
});

describe("timelineTypeLabel", () => {
  it.each([
    ["rsi", "member_joined", "arrivée dans l'org"],
    ["rsi", "member_left", "départ de l'org"],
    ["rsi", "rank_changed", "rang RSI"],
    ["rsi", "roles_changed", "rôles RSI"],
    ["rsi", "handle_changed", "changement de handle"],
    ["rsi", "org_renamed", "org renamed"],
    ["discord", "joined", "arrivée"],
    ["discord", "roles_changed", "rôles"],
  ] as const)("%s %s → « %s »", (source, type, label) => {
    expect(timelineTypeLabel({ source, type })).toBe(label);
  });
});

describe("timelineWhen", () => {
  it("gives the date of an entry whose date is exact", () => {
    expect(timelineWhen({ at: "2026-09-29T08:30:00Z", notBefore: null })).toBe("2026-09-29 08:30 UTC");
  });

  it("gives the window of a Discord event whose exact date is unknown", () => {
    expect(timelineWhen({ at: "2026-09-30T12:00:00Z", notBefore: "2026-09-28T20:00:00Z" })).toBe(
      "entre 2026-09-28 20:00 UTC et 2026-09-30 12:00 UTC",
    );
  });
});
