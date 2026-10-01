import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api/errors";

vi.mock("next/headers", () => ({ cookies: async () => ({ set: vi.fn() }), headers: async () => new Headers() }));
vi.mock("@/lib/auth/session", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/auth/session")>()),
  getSession: vi.fn(async () => ({
    userId: 1,
    username: "pilot",
    isAdmin: false,
    accessToken: "jwt",
    clientIp: "203.0.113.7",
    expiresAt: new Date(),
  })),
}));
vi.mock("@/lib/api/client", () => ({ apiPost: vi.fn(), apiDelete: vi.fn() }));

const { apiDelete, apiPost } = await import("@/lib/api/client");
const { getSession } = await import("@/lib/auth/session");
const { createDiscordIngestKeyAction, revokeApiKeyAction } = await import("./discord-key-actions");

/** What sessionCtx() gives the API client for the mocked session. */
const ctx = { bearerToken: "jwt", clientIp: "203.0.113.7" };

describe("createDiscordIngestKeyAction", () => {
  beforeEach(() => {
    vi.mocked(apiPost).mockReset();
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-01T10:00:00Z"));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("creates a discord:ingest key that expires after the chosen number of days", async () => {
    const created = {
      id: 7,
      name: "Vencord 2026-10-01",
      keyPrefix: "Ab3dE9",
      createdAt: "2026-10-01T10:00:00Z",
      lastUsedAt: null,
      expiresAt: "2027-03-30T10:00:00Z",
      isRevoked: false,
      scope: "discord:ingest",
      rawKey: "Ab3dE9_c2VjcmV0",
    };
    vi.mocked(apiPost).mockResolvedValue(created);

    const result = await createDiscordIngestKeyAction("  Vencord 2026-10-01  ", 180);

    expect(result).toEqual({ ok: true, data: created });
    expect(apiPost).toHaveBeenCalledWith(
      "/api/api-keys",
      { name: "Vencord 2026-10-01", expiresAt: "2027-03-30T10:00:00.000Z", scope: "discord:ingest" },
      ctx,
    );
  });

  it("asks for the longest lifetime the API accepts, 365 days", async () => {
    vi.mocked(apiPost).mockResolvedValue({});

    await createDiscordIngestKeyAction("Vencord", 365);

    expect(vi.mocked(apiPost).mock.calls[0]?.[1]).toEqual({
      name: "Vencord",
      expiresAt: "2027-10-01T10:00:00.000Z",
      scope: "discord:ingest",
    });
  });

  it("reports the API's refusal instead of throwing", async () => {
    vi.mocked(apiPost).mockRejectedValue(new ApiError(400, { title: "Bad Request", status: 400 }));

    expect(await createDiscordIngestKeyAction("Vencord", 30)).toEqual({ ok: false, error: "Bad Request" });
  });

  it("shows the API's useful explanation and keeps transport details private", async () => {
    vi.mocked(apiPost).mockRejectedValueOnce(new ApiError(400, { detail: "Trop de clés actives.", status: 400 }));
    expect(await createDiscordIngestKeyAction("Vencord", 30)).toEqual({ ok: false, error: "Trop de clés actives." });
    vi.mocked(apiPost).mockRejectedValueOnce(new Error("connect failed to private-api.internal:5000"));
    expect(await createDiscordIngestKeyAction("Vencord", 30)).toEqual({ ok: false, error: "Échec de la création de la clé." });
  });

  it("returns a failure when reading the session fails", async () => {
    vi.mocked(getSession).mockRejectedValueOnce(new Error("internal session failure"));
    expect(await createDiscordIngestKeyAction("Vencord", 30)).toEqual({ ok: false, error: "Échec de la création de la clé." });
    expect(apiPost).not.toHaveBeenCalled();
  });
});

describe("revokeApiKeyAction", () => {
  // Braces matter: a function returned by beforeEach runs as its teardown, and
  // mockReset() returns the mock itself.
  beforeEach(() => {
    vi.mocked(apiDelete).mockReset();
  });

  it("revokes the key by its id", async () => {
    vi.mocked(apiDelete).mockResolvedValue(undefined);

    expect(await revokeApiKeyAction(12)).toEqual({ ok: true });
    expect(apiDelete).toHaveBeenCalledWith("/api/api-keys/12", ctx);
  });

  it("reports a key the API does not find for this user", async () => {
    vi.mocked(apiDelete).mockRejectedValue(new ApiError(404, { title: "Not Found", status: 404 }));

    expect(await revokeApiKeyAction(99)).toEqual({ ok: false, error: "Not Found" });
  });

  it("returns a failure when reading the session fails", async () => {
    vi.mocked(getSession).mockRejectedValueOnce(new Error("internal session failure"));
    expect(await revokeApiKeyAction(12)).toEqual({ ok: false, error: "Échec de la révocation." });
    expect(apiDelete).not.toHaveBeenCalled();
  });
});
