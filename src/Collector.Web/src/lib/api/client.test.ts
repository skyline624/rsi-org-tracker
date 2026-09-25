import { afterEach, describe, expect, it, vi } from "vitest";
import { apiFetch, apiUpload } from "./client";

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

  it("passes the browser's IP to the API so it can rate-limit and log the real client", async () => {
    const fetchMock = stubFetch();

    await apiFetch("/api/organizations", { bearerToken: "t", clientIp: "203.0.113.7" });

    const headers = fetchMock.mock.calls[0]![1]!.headers as Record<string, string>;
    expect(headers["X-Forwarded-For"]).toBe("203.0.113.7");
  });

  it("uploads multipart form data with the caller's token and IP", async () => {
    const fetchMock = stubFetch();
    const form = new FormData();
    form.append("file", new Blob(["ID3"]), "a.mp3");

    await apiUpload("/api/users/x/audio", form, { bearerToken: "jwt", clientIp: "203.0.113.7" });

    const [url, init] = fetchMock.mock.calls[0]!;
    const headers = init!.headers as Record<string, string>;
    expect(String(url)).toBe("http://127.0.0.1:5000/api/users/x/audio");
    expect(init!.method).toBe("POST");
    expect(init!.body).toBe(form);
    expect(headers["Authorization"]).toBe("Bearer jwt");
    expect(headers["X-Forwarded-For"]).toBe("203.0.113.7");
    expect(headers["Content-Type"]).toBeUndefined();
  });

  it("forwards the caller's bearer token", async () => {
    const fetchMock = stubFetch();

    await apiFetch("/api/organizations", { bearerToken: "user-jwt" });

    const headers = fetchMock.mock.calls[0]![1]!.headers as Record<string, string>;
    expect(headers["Authorization"]).toBe("Bearer user-jwt");
  });
});
