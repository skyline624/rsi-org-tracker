import { describe, expect, it } from "vitest";

import {
  buildPayload, MAX_BODY_BYTES, MAX_MEMBERS, type SyncMember, type SyncPayload, toSyncRole, utf8ByteLength,
} from "../scTracker.desktop/lib/payload";

const GUILD_ID = "123456789012345678";
const ROLE_ID = "223456789012345678";
const USER_ID = "323456789012345678";

function member(i: number, extra: Partial<SyncMember> = {}): SyncMember {
  return {
    userId: String(400000000000000000n + BigInt(i)),
    username: `pilote${i}`,
    globalName: null,
    nick: null,
    roleIds: [],
    joinedAt: "2025-03-14T20:11:05.123000+00:00",
    bot: false,
    ...extra,
  };
}

function payload(overrides: Partial<SyncPayload> = {}): SyncPayload {
  return {
    pluginVersion: "1.0.0",
    collectedAt: "2026-09-30T12:00:00.000Z",
    collectionDurationMs: 84000,
    guild: { id: GUILD_ID, name: "Ma Corpo", icon: null, memberCount: 1 },
    coverage: { method: "member-search", complete: true, expectedCount: 1, collectedCount: 1 },
    roles: [{ id: ROLE_ID, name: "Officier", position: 12, color: "#e67e22", hoist: true, managed: false }],
    members: [{
      userId: USER_ID, username: "pilote42", globalName: "Pilote", nick: "[CORP] Pilote42",
      roleIds: [ROLE_ID], joinedAt: "2025-03-14T20:11:05.123+00:00", bot: false,
    }],
    ...overrides,
  };
}

function built(p: SyncPayload) {
  const result = buildPayload(p);
  if (!result.ok) throw new Error(`expected ok, got ${result.reason}`);
  return result;
}

/** A Discord client role: extra fields, and permissions as a bigint that JSON.stringify refuses. */
const clientRole = {
  id: ROLE_ID, name: "Officier", position: 12, color: 0xe67e22, colorString: "#E67E22",
  hoist: true, managed: false, permissions: 8n, mentionable: true, flags: 0, guildId: GUILD_ID,
  tags: undefined, icon: undefined, unicodeEmoji: undefined,
};

describe("toSyncRole", () => {
  it("keeps only the tracker's fields of a client role carrying bigint permissions", () => {
    const role = toSyncRole(clientRole);

    expect(role).toEqual({ id: ROLE_ID, name: "Officier", position: 12, color: "#e67e22", hoist: true, managed: false });
    expect(() => JSON.stringify(role)).not.toThrow();
  });

  it("derives the colour from the number when there is no colour string, and null for no colour", () => {
    expect(toSyncRole({ ...clientRole, colorString: undefined, color: 0x3498db }).color).toBe("#3498db");
    expect(toSyncRole({ ...clientRole, colorString: undefined, color: 0x00000f }).color).toBe("#00000f");
    expect(toSyncRole({ ...clientRole, colorString: undefined, color: 0 }).color).toBeNull();
    expect(toSyncRole({ ...clientRole, colorString: "orange", color: 0 }).color).toBeNull();
  });

  it("turns missing flags into false", () => {
    const role = toSyncRole({ id: ROLE_ID, name: "Membre", position: 1 });

    expect(role).toEqual({ id: ROLE_ID, name: "Membre", position: 1, color: null, hoist: false, managed: false });
  });
});

describe("buildPayload", () => {
  it("serialises exactly the ingest contract, with ids as strings", () => {
    const result = built(payload());
    const json = JSON.parse(result.json);

    expect(Object.keys(json)).toEqual(
      ["pluginVersion", "collectedAt", "collectionDurationMs", "guild", "coverage", "roles", "members"]);
    expect(json.guild).toEqual({ id: GUILD_ID, name: "Ma Corpo", icon: null, memberCount: 1 });
    expect(json.coverage).toEqual({ method: "member-search", complete: true, expectedCount: 1, collectedCount: 1 });
    expect(json.roles).toEqual([{ id: ROLE_ID, name: "Officier", position: 12, color: "#e67e22", hoist: true, managed: false }]);
    expect(json.members).toEqual([{
      userId: USER_ID, username: "pilote42", globalName: "Pilote", nick: "[CORP] Pilote42",
      roleIds: [ROLE_ID], joinedAt: "2025-03-14T20:11:05.123+00:00", bot: false,
    }]);
    expect(result.json).toContain(`"userId":"${USER_ID}"`);
    expect(result.bytes).toBe(Buffer.byteLength(result.json));
  });

  it("never serialises permissions nor any other client field, and does not throw on bigints", () => {
    const role = { ...toSyncRole(clientRole), permissions: 8n };
    const clientMember = { ...member(1), avatar: "a_1234", premiumSince: "2024-01-01", flags: 2n, communicationDisabledUntil: null };
    const guild = { id: GUILD_ID, name: "Ma Corpo", icon: null, memberCount: 1, features: new Set(["COMMUNITY"]), maxMembers: 500000n };

    const result = built(payload({ roles: [role], members: [clientMember], guild }));

    expect(result.json).not.toContain("permissions");
    expect(result.json).not.toContain("avatar");
    expect(result.json).not.toContain("premiumSince");
    expect(result.json).not.toContain("flags");
    expect(result.json).not.toContain("features");
    expect(Object.keys(JSON.parse(result.json).members[0])).toEqual(
      ["userId", "username", "globalName", "nick", "roleIds", "joinedAt", "bot"]);
  });

  it("writes null for missing optional values and false unless bot is exactly true", () => {
    const loose = { ...member(1), globalName: undefined, nick: undefined, joinedAt: undefined, bot: "yes" } as unknown as SyncMember;

    const json = JSON.parse(built(payload({ members: [loose, member(2, { bot: true })] })).json);

    expect(json.members[0]).toMatchObject({ globalName: null, nick: null, joinedAt: null, bot: false });
    expect(json.members[1].bot).toBe(true);
  });

  it("sends whole milliseconds for the collection duration", () => {
    const json = JSON.parse(built(payload({ collectionDurationMs: 84000.6 })).json);

    expect(json.collectionDurationMs).toBe(84001);
  });

  it("accepts MAX_MEMBERS members and refuses one more", () => {
    const members = Array.from({ length: MAX_MEMBERS }, (_, i) => member(i));

    expect(buildPayload(payload({ members })).ok).toBe(true);
    expect(buildPayload(payload({ members: [...members, member(MAX_MEMBERS)] })))
      .toEqual({ ok: false, reason: "too_many_members" });
  });

  it("accepts a body of exactly MAX_BODY_BYTES bytes and refuses one byte more", () => {
    const empty = built(payload({ guild: { id: GUILD_ID, name: "", icon: null, memberCount: 1 } }));
    const filler = MAX_BODY_BYTES - empty.bytes;

    const atLimit = buildPayload(payload({ guild: { id: GUILD_ID, name: "a".repeat(filler), icon: null, memberCount: 1 } }));
    const overLimit = buildPayload(payload({ guild: { id: GUILD_ID, name: "a".repeat(filler + 1), icon: null, memberCount: 1 } }));

    expect(atLimit.ok && atLimit.bytes).toBe(MAX_BODY_BYTES);
    expect(overLimit).toEqual({ ok: false, reason: "too_large" });
  });

  it("counts UTF-8 bytes, not characters", () => {
    // 13.1 million characters but 26.2 million bytes: "é" takes two bytes.
    const name = "é".repeat(MAX_BODY_BYTES / 2 + 1000);

    expect(buildPayload(payload({ guild: { id: GUILD_ID, name, icon: null, memberCount: 1 } })))
      .toEqual({ ok: false, reason: "too_large" });
  });
});

describe("utf8ByteLength", () => {
  it.each([
    [""], ["pilote42"], ["Pilote éèà"], ["€ ✓"], ["😀 [CORP] 🚀"], ["a\ud800b"], ["\udc00"], ["\ud83d"],
  ])("agrees with Buffer.byteLength for %j", text => {
    expect(utf8ByteLength(text)).toBe(Buffer.byteLength(text, "utf8"));
  });
});
