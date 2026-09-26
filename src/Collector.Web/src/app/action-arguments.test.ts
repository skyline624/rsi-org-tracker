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
