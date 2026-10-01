import { describe, expect, it } from "vitest";

import type { SyncMember } from "../scTracker.desktop/lib/payload";
import { mergePage, nextAfter, type SearchMember, toSyncMember } from "../scTracker.desktop/lib/searchCursor";

function hit(id: string, joinedAt: string, extra: Partial<SearchMember["member"]> = {}): SearchMember {
  return {
    member: {
      user: { id, username: `user${id.slice(-3)}`, global_name: null },
      nick: null,
      roles: [],
      joined_at: joinedAt,
      ...extra,
    },
  };
}

const A = hit("100000000000000001", "2025-03-14T20:11:05.123+00:00");
const B = hit("100000000000000002", "2025-03-14T22:11:06.000+02:00");
const C = hit("100000000000000003", "2025-03-14T20:11:05.123456+00:00");

describe("nextAfter", () => {
  it("is null for an empty page", () => {
    expect(nextAfter([])).toBeNull();
  });

  it("points after the LAST member of the page, joined_at in integer milliseconds", () => {
    expect(nextAfter([B, A])).toEqual({ guild_joined_at: 1741983065123, user_id: "100000000000000001" });
  });

  it("drops the microseconds Discord sends", () => {
    const after = nextAfter([C]);

    expect(after).toEqual({ guild_joined_at: 1741983065123, user_id: "100000000000000003" });
    expect(Number.isInteger(after?.guild_joined_at)).toBe(true);
  });

  it("is null when the last joined_at cannot be read, so the collection stops instead of restarting", () => {
    expect(nextAfter([A, hit("100000000000000004", "not a date")])).toBeNull();
  });

  it("returns the same cursor for a page of members already seen, which the caller detects", () => {
    expect(nextAfter([A, B])).toEqual(nextAfter([A, B]));
  });
});

describe("mergePage", () => {
  it("adds new members and reports how many", () => {
    const seen = new Map<string, SyncMember>();

    expect(mergePage(seen, [A, B])).toEqual({ added: 2 });
    expect([...seen.keys()]).toEqual(["100000000000000001", "100000000000000002"]);
  });

  it("dedupes by user id, within a page and across pages, keeping the first copy", () => {
    const seen = new Map<string, SyncMember>();
    mergePage(seen, [A]);

    const renamed = hit("100000000000000001", "2025-03-14T20:11:05.123+00:00", { nick: "Renamed" });
    expect(mergePage(seen, [renamed, B, B])).toEqual({ added: 1 });
    expect(seen.size).toBe(2);
    expect(seen.get("100000000000000001")?.nick).toBeNull();
  });

  it("reports 0 for a page of ids already seen (a stuck cursor)", () => {
    const seen = new Map<string, SyncMember>();
    mergePage(seen, [A, B]);

    expect(mergePage(seen, [A, B])).toEqual({ added: 0 });
    expect(seen.size).toBe(2);
  });
});

describe("toSyncMember", () => {
  it("keeps the later join when the same ID appears across pages", () => {
    const seen = new Map<string, SyncMember>();
    mergePage(seen, [A]);
    const newer = hit(A.member.user.id, "2026-01-01T00:00:00Z", { nick: "Returned" });
    expect(mergePage(seen, [newer])).toEqual({ added: 0 });
    mergePage(seen, [A]);
    expect(seen.get(A.member.user.id)?.nick).toBe("Returned");
  });
  it("maps every field the tracker keeps", () => {
    const m = hit("100000000000000001", "2025-03-14T20:11:05.123000+00:00", {
      user: { id: "100000000000000001", username: "pilote42", global_name: "Pilote", bot: false },
      nick: "[CORP] Pilote42",
      roles: ["200000000000000001", "200000000000000002"],
    });

    expect(toSyncMember(m)).toEqual({
      userId: "100000000000000001",
      username: "pilote42",
      globalName: "Pilote",
      nick: "[CORP] Pilote42",
      roleIds: ["200000000000000001", "200000000000000002"],
      joinedAt: "2025-03-14T20:11:05.123000+00:00",
      bot: false,
    });
  });

  it("turns absent optional fields into null and bot into a strict boolean", () => {
    const bare = { member: { user: { id: "100000000000000009", username: "bare" }, roles: [], joined_at: "2025-01-01T00:00:00+00:00" } };

    expect(toSyncMember(bare)).toMatchObject({ globalName: null, nick: null, bot: false });
    expect(toSyncMember(hit("100000000000000008", "2025-01-01T00:00:00+00:00", {
      user: { id: "100000000000000008", username: "helper", bot: true },
    })).bot).toBe(true);
    const truthy = { member: { ...bare.member, user: { ...bare.member.user, bot: "true" } } } as unknown as SearchMember;
    expect(toSyncMember(truthy).bot).toBe(false);
  });

  it("copies the role list instead of sharing Discord's array", () => {
    const roles = ["200000000000000001"];
    const mapped = toSyncMember(hit("100000000000000001", "2025-01-01T00:00:00+00:00", { roles }));

    roles.push("200000000000000002");

    expect(mapped.roleIds).toEqual(["200000000000000001"]);
  });

  it("keeps a missing joined_at as null", () => {
    const noDate = { member: { ...A.member, joined_at: null } } as unknown as SearchMember;

    expect(toSyncMember(noDate).joinedAt).toBeNull();
  });
});
