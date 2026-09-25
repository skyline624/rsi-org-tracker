import { beforeEach, describe, expect, it, vi } from "vitest";

const cookieValues = new Map<string, string>();
vi.mock("next/headers", () => ({
  cookies: async () => ({
    get: (name: string) =>
      cookieValues.has(name) ? { name, value: cookieValues.get(name)! } : undefined,
  }),
}));
vi.mock("./jwt", () => ({ verifyAccessToken: vi.fn() }));

const { verifyAccessToken } = await import("./jwt");
const { getSession } = await import("./session");

// A well-formed but unsigned token: decoding it would yield an admin session.
const forged =
  "eyJhbGciOiJub25lIn0." +
  Buffer.from(
    JSON.stringify({ sub: "1", unique_name: "admin", role: "Admin", exp: 4102444800 }),
  ).toString("base64url") +
  ".";

describe("getSession", () => {
  beforeEach(() => {
    cookieValues.clear();
    vi.mocked(verifyAccessToken).mockReset();
  });

  it("returns null for a token whose signature does not verify", async () => {
    cookieValues.set("sct_access", forged);
    vi.mocked(verifyAccessToken).mockResolvedValue(null);

    expect(await getSession()).toBeNull();
  });

  it("builds the session from the verified claims", async () => {
    cookieValues.set("sct_access", "signed-token");
    vi.mocked(verifyAccessToken).mockResolvedValue({
      sub: "7",
      unique_name: "pilot",
      "http://schemas.microsoft.com/ws/2008/06/identity/claims/role": "Admin",
      exp: 4102444800,
    });

    const session = await getSession();

    expect(verifyAccessToken).toHaveBeenCalledWith("signed-token");
    expect(session).toMatchObject({
      userId: 7,
      username: "pilot",
      isAdmin: true,
      accessToken: "signed-token",
    });
  });

  it("returns null without an access cookie", async () => {
    expect(await getSession()).toBeNull();
  });
});
