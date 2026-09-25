import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("next/headers", () => ({ headers: async () => new Headers() }));
vi.mock("@/lib/auth/session", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/auth/session")>()),
  getSession: vi.fn(),
}));
vi.mock("@/lib/api/client", () => ({ apiPost: vi.fn(async () => ({})) }));

const { getSession } = await import("@/lib/auth/session");
const { apiPost } = await import("@/lib/api/client");
const { createEntityAction, createOrganizationAction } = await import("./actions");

const member = { userId: 2, username: "member", isAdmin: false, accessToken: "jwt", expiresAt: new Date() };

describe("manual additions (open to every signed-in account)", () => {
  beforeEach(() => {
    vi.mocked(apiPost).mockClear();
    vi.mocked(getSession).mockResolvedValue(member);
  });

  it("lets a non-admin member add an organization", async () => {
    const result = await createOrganizationAction({ sid: "NEWORG", name: "New org" });

    expect(result).toEqual({ ok: true });
    expect(apiPost).toHaveBeenCalledWith(
      "/api/admin/organizations",
      expect.objectContaining({ sid: "NEWORG" }),
      expect.objectContaining({ bearerToken: "jwt" }),
    );
  });

  it("lets a non-admin member add a person", async () => {
    expect(await createEntityAction({ handle: "redacted-pilot" })).toEqual({ ok: true });
  });

  it("still refuses anonymous callers", async () => {
    vi.mocked(getSession).mockResolvedValue(null);

    expect((await createOrganizationAction({ sid: "X", name: "X" })).ok).toBe(false);
    expect(apiPost).not.toHaveBeenCalled();
  });
});
