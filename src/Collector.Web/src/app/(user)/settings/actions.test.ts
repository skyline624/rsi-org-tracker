import { beforeEach, describe, expect, it, vi } from "vitest";

const setCookie = vi.fn();
vi.mock("next/headers", () => ({ cookies: async () => ({ set: setCookie }) }));
vi.mock("@/lib/auth/session", () => ({
  getSession: vi.fn(async () => ({ userId: 1, username: "pilot", isAdmin: false, accessToken: "old" })),
}));
vi.mock("@/lib/api/client", () => ({ apiPost: vi.fn() }));

const { apiPost } = await import("@/lib/api/client");
const { changePasswordAction } = await import("./actions");

describe("changePasswordAction", () => {
  beforeEach(() => {
    setCookie.mockReset();
    vi.mocked(apiPost).mockReset();
  });

  it("stores the fresh tokens returned by the API (the old session is revoked)", async () => {
    vi.mocked(apiPost).mockResolvedValue({
      accessToken: "new-access",
      refreshToken: "new-refresh",
      expiresAt: "2030-01-01T00:00:00Z",
      user: { id: 1, username: "pilot" },
    });

    const result = await changePasswordAction("old password", "a brand new passphrase");

    expect(result).toEqual({ ok: true });
    expect(setCookie).toHaveBeenCalledWith("sct_access", "new-access", expect.objectContaining({ httpOnly: true }));
    expect(setCookie).toHaveBeenCalledWith("sct_refresh", "new-refresh", expect.objectContaining({ httpOnly: true }));
  });
});
