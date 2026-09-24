import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api/errors";

vi.mock("next/navigation", () => ({
  redirect: vi.fn((url: string) => {
    throw new Error(`REDIRECT:${url}`);
  }),
}));
vi.mock("@/lib/auth/session", () => ({ getSession: vi.fn() }));

const { getSession } = await import("@/lib/auth/session");
const { requireAuthCtx, withAuthRedirect, redirectIfUnauthorized } = await import(
  "./server-api"
);

describe("server-api auth helpers", () => {
  beforeEach(() => vi.mocked(getSession).mockReset());

  it("redirects to /login when there is no session", async () => {
    vi.mocked(getSession).mockResolvedValue(null);

    await expect(requireAuthCtx()).rejects.toThrow("REDIRECT:/login");
  });

  it("returns the user's bearer token as the API context", async () => {
    vi.mocked(getSession).mockResolvedValue({
      userId: 1,
      username: "pilot",
      isAdmin: false,
      accessToken: "user-jwt",
      expiresAt: new Date(Date.now() + 60_000),
    });

    await expect(requireAuthCtx()).resolves.toEqual({ bearerToken: "user-jwt" });
  });

  it("turns an API 401 into a redirect to /login", async () => {
    const rejected = Promise.reject(new ApiError(401, { title: "Unauthorized" }));

    await expect(withAuthRedirect(rejected)).rejects.toThrow("REDIRECT:/login");
  });

  it("rethrows other API errors untouched", async () => {
    const notFound = new ApiError(404, { title: "Not found" });

    await expect(withAuthRedirect(Promise.reject(notFound))).rejects.toBe(notFound);
  });

  it("redirects when any settled call was rejected with a 401", () => {
    const results: PromiseSettledResult<unknown>[] = [
      { status: "fulfilled", value: 1 },
      { status: "rejected", reason: new ApiError(401, {}) },
    ];

    expect(() => redirectIfUnauthorized(results)).toThrow("REDIRECT:/login");
  });

  it("ignores non-auth failures among settled calls", () => {
    const results: PromiseSettledResult<unknown>[] = [
      { status: "rejected", reason: new ApiError(500, {}) },
    ];

    expect(() => redirectIfUnauthorized(results)).not.toThrow();
  });
});
