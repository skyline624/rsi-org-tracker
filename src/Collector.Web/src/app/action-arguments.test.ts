import { beforeEach, describe, expect, it, vi } from "vitest";

// Server actions can be called by any client with any values: TypeScript types are gone
// at run time. A malformed call must get an answer, never a 500, and never reach the API.
vi.mock("next/headers", () => ({ cookies: async () => ({ set: vi.fn() }), headers: async () => new Headers() }));
vi.mock("@/lib/auth/session", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/auth/session")>()),
  getSession: vi.fn(async () => ({ userId: 1, username: "admin", isAdmin: true, accessToken: "jwt", expiresAt: new Date() })),
}));
vi.mock("@/lib/api/client", () => ({
  apiGet: vi.fn(async () => ({ items: [] })),
  apiPost: vi.fn(async () => ({})),
  apiPut: vi.fn(async () => ({})),
  apiDelete: vi.fn(async () => ({})),
}));

const api = await import("@/lib/api/client");
const { INVALID_ARGUMENTS } = await import("@/lib/validation");
const { changePasswordAction } = await import("./(user)/settings/actions");
const { setDiscordTokenAction } = await import("./(user)/settings/discord-token-actions");
const { createDiscordIngestKeyAction, revokeApiKeyAction } = await import("./(user)/settings/discord-key-actions");
const discord = await import("./(public)/discord/actions");
const { searchOrgsAction } = await import("./(public)/users/[handle]/membership-actions");
const { createEntityAction, createOrganizationAction } = await import("./(user)/admin/actions");
const { createAccountAction, setUserFlagsAction } = await import("./(user)/accounts/actions");

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const anyValue = (v: unknown) => v as any;
const invalid = { ok: false, error: INVALID_ARGUMENTS };

describe("server actions reject malformed arguments", () => {
  beforeEach(() => vi.clearAllMocks());

  const noApiCall = () => {
    expect(api.apiGet).not.toHaveBeenCalled();
    expect(api.apiPost).not.toHaveBeenCalled();
    expect(api.apiPut).not.toHaveBeenCalled();
    expect(api.apiDelete).not.toHaveBeenCalled();
  };

  it.each([
    ["a number as password", () => changePasswordAction(anyValue(123), "a new passphrase")],
    ["a huge new password", () => changePasswordAction("current", "x".repeat(10_000))],
  ])("changePasswordAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["no token", () => setDiscordTokenAction(anyValue(null))],
    ["an object", () => setDiscordTokenAction(anyValue({ token: "x" }))],
  ])("setDiscordTokenAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["no name", () => createDiscordIngestKeyAction(undefined, 180)],
    ["a blank name", () => createDiscordIngestKeyAction("   ", 180)],
    ["a name too long", () => createDiscordIngestKeyAction("x".repeat(101), 180)],
    ["an expiry as text", () => createDiscordIngestKeyAction("Vencord", "180")],
    ["an expiry of zero days", () => createDiscordIngestKeyAction("Vencord", 0)],
    ["an expiry beyond 365 days", () => createDiscordIngestKeyAction("Vencord", 366)],
    ["a fractional expiry", () => createDiscordIngestKeyAction("Vencord", 1.5)],
  ])("createDiscordIngestKeyAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a path as id", () => revokeApiKeyAction("../admin/users/3")],
    ["an id as text", () => revokeApiKeyAction("12")],
    ["a negative id", () => revokeApiKeyAction(-1)],
  ])("revokeApiKeyAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  // Discord ids end up in API paths: only snowflakes get through.
  const GUILD = "123456789012345678";
  const ROLE = "223456789012345678";
  const USER = "323456789012345678";

  it.each([
    ["a path as server id", () => discord.mapGuildOrgAction("../admin", "CORP")],
    ["a server id as a number", () => discord.mapGuildOrgAction(123456789012, "CORP")],
    ["a SID with a path", () => discord.mapGuildOrgAction(GUILD, "../x")],
    ["a SID too long", () => discord.mapGuildOrgAction(GUILD, "WAYTOOLONGSID")],
    ["no SID (null unmaps)", () => discord.mapGuildOrgAction(GUILD, undefined)],
  ])("mapGuildOrgAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a path as role id", () => discord.updateGuildRoleAction(GUILD, "../1", true, 1, null)],
    ["a text rank flag", () => discord.updateGuildRoleAction(GUILD, ROLE, "true", 1, null)],
    ["a negative order", () => discord.updateGuildRoleAction(GUILD, ROLE, true, -1, null)],
    ["an order beyond 1000", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1001, null)],
    ["a fractional order", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1.5, null)],
    ["an order as text", () => discord.updateGuildRoleAction(GUILD, ROLE, true, "3", null)],
    ["no order (null clears it)", () => discord.updateGuildRoleAction(GUILD, ROLE, true, undefined, null)],
    ["an RSI rank too long", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1, "x".repeat(101))],
    ["an RSI rank as a number", () => discord.updateGuildRoleAction(GUILD, ROLE, true, 1, 42)],
  ])("updateGuildRoleAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  const linkCases = [
    ["a path as account id", "../1", 42, "Pilote42"],
    ["an account id too short", "1234", 42, "Pilote42"],
    ["a citizen number as text", USER, "42", "Pilote42"],
    ["a negative citizen number", USER, -1, "Pilote42"],
    ["no citizen number (null expected)", USER, undefined, "Pilote42"],
    ["a handle with a path", USER, 42, "../admin"],
    ["no handle", USER, null, undefined],
  ] as const;

  it.each(linkCases)("acceptSuggestionAction: %s", async (_, user, citizen, handle) => {
    expect(await discord.acceptSuggestionAction(user, citizen, handle)).toEqual(invalid);
    noApiCall();
  });

  it.each(linkCases)("rejectSuggestionAction: %s", async (_, user, citizen, handle) => {
    expect(await discord.rejectSuggestionAction(user, citizen, handle)).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["an id as text", () => discord.undoRejectionAction("12")],
    ["a negative id", () => discord.undoRejectionAction(-1)],
    ["a path as id", () => discord.undoRejectionAction("../admin")],
  ])("undoRejectionAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([anyValue({}), anyValue(null), "x".repeat(500)])("searchOrgsAction(%s) finds nothing", async (query) => {
    expect(await searchOrgsAction(query)).toEqual([]);
    noApiCall();
  });

  it.each([
    ["no input", () => createEntityAction(anyValue(null))],
    ["a handle with spaces", () => createEntityAction({ handle: "bad handle!" })],
    ["a negative citizen number", () => createEntityAction({ citizenId: -1 })],
    ["a text citizen number", () => createEntityAction({ citizenId: anyValue("12") })],
  ])("createEntityAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["no input", () => createOrganizationAction(anyValue(undefined))],
    ["a SID too long", () => createOrganizationAction({ sid: "TOOLONGSID12", name: "Org" })],
    ["an empty name", () => createOrganizationAction({ sid: "ORG", name: "  " })],
    ["a script URL as image", () => createOrganizationAction({ sid: "ORG", name: "Org", urlImage: "javascript:alert(1)" })],
  ])("createOrganizationAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a number as username", () => createAccountAction(anyValue({ username: 5, email: "a@b.c", password: "long enough", isAdmin: false }))],
    ["an invalid email", () => createAccountAction({ username: "pilot", email: "not-an-email", password: "long enough", isAdmin: false })],
    ["a text admin flag", () => createAccountAction(anyValue({ username: "pilot", email: "a@b.c", password: "long enough", isAdmin: "yes" }))],
    ["text flags", () => setUserFlagsAction(1, anyValue({ isAdmin: "yes" }))],
  ])("accounts: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it("still passes well-formed arguments through", async () => {
    expect(await createOrganizationAction({ sid: "ORG", name: "Org", urlImage: "https://robertsspaceindustries.com/media/x.png" }))
      .toEqual({ ok: true });
    expect(await createEntityAction({ handle: "redacted-pilot", citizenId: 42 })).toEqual({ ok: true });
    expect(api.apiPost).toHaveBeenCalledTimes(2);
  });
});
