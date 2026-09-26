import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("@/lib/auth/session", () => ({
  getSession: vi.fn(async () => ({ userId: 1, username: "pilot", isAdmin: false, accessToken: "jwt", expiresAt: new Date() })),
}));

const { GET } = await import("./route");

describe("audio stream proxy", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.unstubAllEnvs();
  });

  it("reaches the API on the loopback when API_BASE_URL is not set, like every other call", async () => {
    vi.stubEnv("API_BASE_URL", undefined as unknown as string);
    const fetchMock = vi.fn(async () => new Response("audio", { status: 200, headers: { "content-type": "audio/mpeg" } }));
    vi.stubGlobal("fetch", fetchMock);

    const res = await GET(new Request("http://localhost/api/audio/7"), { params: Promise.resolve({ id: "7" }) });

    expect(res.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledWith("http://127.0.0.1:5000/api/audio/7", expect.anything());
  });
});
