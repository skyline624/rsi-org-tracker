import { describe, expect, it } from "vitest";
import {
  parseDiscordTab,
  parseEventFilters,
  parseMemberFilters,
  parseRankOrderInput,
  tabHref,
} from "./params";

const GUILD = "123456789012345678";
const ROLE = "223456789012345678";
const USER = "323456789012345678";

describe("parseDiscordTab", () => {
  it.each([
    [undefined, "members"],
    ["members", "members"],
    ["history", "history"],
    ["gaps", "gaps"],
    ["suggestions", "suggestions"],
    ["config", "config"],
    ["syncs", "syncs"],
    [["syncs", "config"], "syncs"],
    ["HISTORY", "members"],
    ["__proto__", "members"],
    ["", "members"],
  ])("%j → %s", (raw, tab) => {
    expect(parseDiscordTab(raw)).toBe(tab);
  });
});

describe("parseMemberFilters", () => {
  it("defaults to the members present, first page", () => {
    expect(parseMemberFilters({})).toEqual({ status: "active", page: 1 });
  });

  it("reads every filter of the members tab", () => {
    expect(
      parseMemberFilters({
        status: "former",
        search: "  pilote ",
        rankRoleId: ROLE,
        reconciliation: "rank_mismatch",
        page: "3",
      }),
    ).toEqual({ status: "former", search: "pilote", rankRoleId: ROLE, reconciliation: "rank_mismatch", page: 3 });
  });

  it("drops what the API would not understand", () => {
    expect(
      parseMemberFilters({ status: "banned", search: "   ", rankRoleId: "../1", reconciliation: "evil", page: "-2" }),
    ).toEqual({ status: "active", page: 1 });
  });

  it("ignores a search longer than 100 characters", () => {
    expect(parseMemberFilters({ search: "x".repeat(101) }).search).toBeUndefined();
    expect(parseMemberFilters({ search: "x".repeat(100) }).search).toBe("x".repeat(100));
  });

  it("keeps the first of a repeated parameter", () => {
    expect(parseMemberFilters({ status: ["all", "former"], page: ["2", "9"] })).toEqual({ status: "all", page: 2 });
  });
});

describe("parseEventFilters", () => {
  it("reads the event type and the account", () => {
    expect(parseEventFilters({ type: "roles_changed", userId: USER })).toEqual({ type: "roles_changed", userId: USER });
  });

  it("drops an unknown type and an account id that is not a snowflake", () => {
    expect(parseEventFilters({ type: "boost", userId: "../admin" })).toEqual({});
  });
});

describe("tabHref", () => {
  it("links to a tab, the members tab being the page itself", () => {
    expect(tabHref(GUILD, "members")).toBe(`/discord/${GUILD}`);
    expect(tabHref(GUILD, "config")).toBe(`/discord/${GUILD}?tab=config`);
    expect(tabHref(GUILD, "history", { userId: USER })).toBe(`/discord/${GUILD}?tab=history&userId=${USER}`);
  });

  it("encodes the server id", () => {
    expect(tabHref("1/2", "syncs")).toBe("/discord/1%2F2?tab=syncs");
  });
});

describe("parseRankOrderInput", () => {
  it.each([
    ["", null],
    ["  ", null],
    ["0", 0],
    [" 12 ", 12],
    ["1000", 1000],
  ])("accepts %j as %j", (raw, value) => {
    expect(parseRankOrderInput(raw)).toEqual({ ok: true, value });
  });

  it.each(["1001", "-1", "1.5", "abc", "1e3", "12345"])("refuses %j", (raw) => {
    expect(parseRankOrderInput(raw)).toEqual({ ok: false });
  });
});
