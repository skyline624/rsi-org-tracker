import { describe, expect, it, vi } from "vitest";
import { refreshWithApi } from "./api-refresh";

const tokens = {
  accessToken: "a",
  refreshToken: "r",
  expiresAt: "2026-09-25T12:00:00Z",
};

const answering = (status: number, body: unknown = {}) =>
  vi.fn(async () => new Response(JSON.stringify(body), { status }));

describe("refreshWithApi", () => {
  it("returns the new tokens", async () => {
    expect(await refreshWithApi("rt", undefined, answering(200, tokens))).toEqual({
      status: "ok",
      tokens,
    });
  });

  it("forwards the client IP", async () => {
    const fetchImpl = answering(200, tokens);

    await refreshWithApi("rt", "203.0.113.7", fetchImpl);

    expect(fetchImpl).toHaveBeenCalledWith(
      expect.stringMatching(/\/api\/auth\/refresh$/),
      expect.objectContaining({
        headers: expect.objectContaining({ "X-Forwarded-For": "203.0.113.7" }),
      }),
    );
  });

  it.each([400, 401, 403])("is rejected when the API refuses the token (%i)", async (status) => {
    expect(await refreshWithApi("rt", undefined, answering(status))).toEqual({ status: "rejected" });
  });

  it.each([429, 500, 502, 503])(
    "is unavailable, not rejected, when the API cannot answer (%i)",
    async (status) => {
      expect(await refreshWithApi("rt", undefined, answering(status))).toEqual({
        status: "unavailable",
      });
    },
  );

  it("is unavailable when the API cannot be reached", async () => {
    const down = vi.fn(async () => {
      throw new TypeError("fetch failed");
    });

    expect(await refreshWithApi("rt", undefined, down)).toEqual({ status: "unavailable" });
  });

  it("is unavailable when a success carries no tokens", async () => {
    expect(await refreshWithApi("rt", undefined, answering(200, {}))).toEqual({
      status: "unavailable",
    });
  });
});
