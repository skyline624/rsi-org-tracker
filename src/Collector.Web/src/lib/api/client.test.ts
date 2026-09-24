import { afterEach, describe, expect, it, vi } from "vitest";
import { apiFetch } from "./client";

describe("apiFetch", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.unstubAllEnvs();
  });

  function stubFetch() {
    const fetchMock = vi.fn(
      async (_url: string | URL | Request, _init?: RequestInit) =>
        new Response("{}", { status: 200 }),
    );
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  it("never sends the internal admin key, even when the legacy serverSide flag is passed", async () => {
    vi.stubEnv("API_INTERNAL_KEY", "admin-key-must-not-leak");
    const fetchMock = stubFetch();

    await apiFetch("/api/organizations", {
      serverSide: true,
    } as Parameters<typeof apiFetch>[1]);

    const headers = fetchMock.mock.calls[0]![1]!.headers as Record<string, string>;
    expect(Object.keys(headers).map((h) => h.toLowerCase())).not.toContain("x-api-key");
  });

  it("forwards the caller's bearer token", async () => {
    const fetchMock = stubFetch();

    await apiFetch("/api/organizations", { bearerToken: "user-jwt" });

    const headers = fetchMock.mock.calls[0]![1]!.headers as Record<string, string>;
    expect(headers["Authorization"]).toBe("Bearer user-jwt");
  });
});
