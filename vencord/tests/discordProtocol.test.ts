import { describe, expect, it } from "vitest";

import { chunksFromAction, DiscordProtocolError, freshMember, restReply, roleCounts, roleIds, searchPage } from "../scTracker.desktop/lib/discordProtocol";

const id = "100000000000000001";
const member = { user: { id, username: "pilot", global_name: "Nom", bot: true }, roles: [], nick: "Pseudo", joined_at: null };
describe("undocumented Discord protocol guards", () => {
  it.each([null, [], { status: 200 }, { status: "200", body: {} }, { status: 600, body: {} }])("rejects an unknown REST wrapper %s", value => {
    expect(() => restReply(value)).toThrow(DiscordProtocolError);
  });
  it("reads finite retry_after and keeps the body opaque until endpoint validation", () => {
    expect(restReply({ status: 429, body: { retry_after: 2.5 } })).toEqual({ status: 429, body: { retry_after: 2.5 }, retryAfter: 2.5 });
    expect(restReply({ status: 429, body: { retry_after: Infinity } }).retryAfter).toBeUndefined();
  });
  it("copies only fresh raw member fields", () => {
    expect(freshMember({ ...member, permissions: 123n, secrets: "never" })).toEqual(member);
    expect(JSON.stringify(freshMember(member))).not.toContain("permissions");
  });
  it("uses Unicode code points for all member names", () => {
    const name = "🚀".repeat(32);
    expect(freshMember({ ...member, nick: name, user: { ...member.user, username: name, global_name: name } })).not.toBeNull();
    expect(freshMember({ ...member, nick: name + "🚀" })).toBeNull();
  });
  it.each([{ ...member, roles: [42] }, { ...member, joined_at: "nonsense" }, { ...member, user: { id: 42, username: "bad" } }, { ...member, nick: "x".repeat(33) }])("rejects malformed member data", value => {
    expect(freshMember(value)).toBeNull();
  });
  it("accepts search hits only under member and validates expected count", () => {
    expect(searchPage({ members: [{ member }], total_result_count: 1 }).members).toHaveLength(1);
    expect(() => searchPage({ members: [{ member }], total_result_count: -1 })).toThrow(DiscordProtocolError);
  });
  it("supports the documented count map and two role-ID envelopes", () => {
    expect(roleCounts({ [id]: 12 })).toEqual({ [id]: 12 });
    expect(roleIds([id, id])).toEqual([id]); expect(roleIds({ member_ids: [id] })).toEqual([id]);
    expect(() => roleIds(Array(101).fill(id))).toThrow(DiscordProtocolError);
  });
  it("ignores malformed chunk envelopes instead of consulting cached members", () => {
    expect(chunksFromAction({ chunks: [{ guildId: id, members: [{ user: { id } }] }] })).toEqual([]);
    expect(chunksFromAction({ chunks: [{ guildId: id, nonce: 42, members: [member] }] })).toEqual([]);
    expect(chunksFromAction({ chunks: [{ guildId: id, nonce: "n", members: [member] }] })[0]?.nonce).toBe("n");
  });
});
