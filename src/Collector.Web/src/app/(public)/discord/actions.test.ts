import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("next/headers", () => ({ cookies: async () => ({ set: vi.fn() }), headers: async () => new Headers() }));
vi.mock("next/cache", () => ({ revalidatePath: vi.fn() }));
vi.mock("@/lib/auth/session", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/auth/session")>()),
  getSession: vi.fn(),
}));
vi.mock("@/lib/api/client", () => ({ apiGet: vi.fn(), apiPost: vi.fn(), apiPut: vi.fn(), apiDelete: vi.fn() }));

const { getSession } = await import("@/lib/auth/session");
const { apiDelete, apiGet, apiPost, apiPut } = await import("@/lib/api/client");
const { ApiError } = await import("@/lib/api/errors");
const { revalidatePath } = await import("next/cache");
const { INVALID_ARGUMENTS } = await import("@/lib/validation");
const {
  acceptSuggestionAction,
  mapGuildOrgAction,
  rejectSuggestionAction,
  searchGuildOrgsAction,
  undoRejectionAction,
  updateGuildRoleAction,
} = await import("./actions");

const GUILD = "123456789012345678";
const ROLE = "223456789012345678";
const USER = "323456789012345678";

const member = {
  userId: 2,
  username: "pilot",
  isAdmin: false,
  accessToken: "jwt",
  clientIp: "203.0.113.7",
  expiresAt: new Date(),
};

/** What sessionCtx() gives the API client for these sessions. */
const ctx = { bearerToken: "jwt", clientIp: "203.0.113.7" };

function expectNoApiCall() {
  for (const fn of [apiGet, apiPost, apiPut, apiDelete]) expect(fn).not.toHaveBeenCalled();
}

// Braces matter: a function returned by beforeEach runs as its teardown.
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(getSession).mockResolvedValue(member);
});

describe("mapGuildOrgAction", () => {
  it("maps the server to the SID typed, trimmed", async () => {
    const summary = { guildId: GUILD, orgSid: "CORP" };
    vi.mocked(apiPut).mockResolvedValue(summary);

    expect(await mapGuildOrgAction(GUILD, " CORP ")).toEqual({ ok: true, data: summary });
    expect(apiPut).toHaveBeenCalledWith(`/api/discord/guilds/${GUILD}/org`, { orgSid: "CORP" }, ctx);
    expect(vi.mocked(revalidatePath).mock.calls).toEqual([["/discord", "layout"], ["/orgs", "layout"]]);
  });

  it("unmaps the server with null", async () => {
    vi.mocked(apiPut).mockResolvedValue({ guildId: GUILD, orgSid: null });

    expect((await mapGuildOrgAction(GUILD, null)).ok).toBe(true);
    expect(apiPut).toHaveBeenCalledWith(`/api/discord/guilds/${GUILD}/org`, { orgSid: null }, ctx);
  });

  it("shows the API's own reason when another user is responsible for the server", async () => {
    const reason = "Ce serveur est relié par alice : seuls ce responsable et les administrateurs peuvent le modifier.";
    vi.mocked(apiPut).mockRejectedValue(new ApiError(403, { title: "Forbidden", status: 403, detail: reason }));

    expect(await mapGuildOrgAction(GUILD, "CORP")).toEqual({ ok: false, error: reason });
    expect(revalidatePath).not.toHaveBeenCalled();
  });
});

describe("updateGuildRoleAction", () => {
  it("sends the rank settings, with the RSI rank trimmed", async () => {
    const role = { roleId: ROLE, isRank: true, rankOrder: 5, rsiRankLabel: "Officer" };
    vi.mocked(apiPut).mockResolvedValue(role);

    expect(await updateGuildRoleAction(GUILD, ROLE, true, 5, "  Officer ")).toEqual({ ok: true, data: role });
    expect(apiPut).toHaveBeenCalledWith(
      `/api/discord/guilds/${GUILD}/roles/${ROLE}`,
      { isRank: true, rankOrder: 5, rsiRankLabel: "Officer" },
      ctx,
    );
  });

  it("clears the order and a blank RSI rank with null", async () => {
    vi.mocked(apiPut).mockResolvedValue({});

    await updateGuildRoleAction(GUILD, ROLE, false, null, "   ");

    expect(apiPut).toHaveBeenCalledWith(
      `/api/discord/guilds/${GUILD}/roles/${ROLE}`,
      { isRank: false, rankOrder: null, rsiRankLabel: null },
      ctx,
    );
  });
});

describe("suggestions", () => {
  it("validates a suggestion as a link to the citizen", async () => {
    vi.mocked(apiPost).mockResolvedValue({ entityId: 7, handle: "Pilote42" });

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({
      ok: true,
      data: { entityId: 7, handle: "Pilote42" },
    });
    expect(apiPost).toHaveBeenCalledWith(
      "/api/discord/links",
      { discordUserId: USER, citizenId: 42, handle: "Pilote42" },
      ctx,
    );
    expect(vi.mocked(revalidatePath).mock.calls).toEqual([["/discord", "layout"], ["/users", "layout"], ["/orgs", "layout"]]);
  });

  it("validates a suggestion for a citizen without a known number", async () => {
    vi.mocked(apiPost).mockResolvedValue({ entityId: 8, handle: "pilote42" });

    await acceptSuggestionAction(USER, null, "pilote42");

    expect(apiPost).toHaveBeenCalledWith(
      "/api/discord/links",
      { discordUserId: USER, citizenId: null, handle: "pilote42" },
      ctx,
    );
  });

  it("ignores a suggestion and returns the rejection id", async () => {
    vi.mocked(apiPost).mockResolvedValue({ id: 9 });

    expect(await rejectSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: true, data: { id: 9 } });
    expect(apiPost).toHaveBeenCalledWith(
      "/api/discord/link-rejections",
      { discordUserId: USER, citizenId: 42, handle: "Pilote42" },
      ctx,
    );
  });

  it("undoes a rejection by its id", async () => {
    vi.mocked(apiDelete).mockResolvedValue(undefined);

    expect(await undoRejectionAction(9)).toEqual({ ok: true });
    expect(apiDelete).toHaveBeenCalledWith("/api/discord/link-rejections/9", ctx);
  });
});

describe("organization search", () => {
  it("proposes an exact SID before ten other matching organizations", async () => {
    const exact = { sid: "NEW", name: "Nouvelle Organisation" };
    const others = Array.from({ length: 10 }, (_, i) => ({ sid: `AN${i}`, name: `New group ${i}` }));
    vi.mocked(apiGet).mockImplementation(async path => path === "/api/organizations/NEW" ? exact : { items: others });
    expect(await searchGuildOrgsAction(" new ")).toEqual([exact, ...others]);
    expect(apiGet).toHaveBeenCalledWith("/api/organizations/NEW", undefined, ctx);
    expect(apiGet).toHaveBeenCalledWith("/api/organizations", { search: "new", pageSize: 10 }, ctx);
  });

  it("finds a single-character SID and deduplicates the listing match", async () => {
    const org = { sid: "X", name: "Corpo X" };
    vi.mocked(apiGet).mockImplementation(async path => path === "/api/organizations/X" ? org : { items: [org] });
    expect(await searchGuildOrgsAction("x")).toEqual([org]);
  });

  it("searches a full name without making an invalid SID request", async () => {
    vi.mocked(apiGet).mockResolvedValue({ items: [{ sid: "NEW", name: "Nouvelle Organisation" }] });
    expect(await searchGuildOrgsAction("Nouvelle Organisation")).toEqual([{ sid: "NEW", name: "Nouvelle Organisation" }]);
    expect(apiGet).toHaveBeenCalledTimes(1);
  });

  it("falls back to names when the exact SID does not exist", async () => {
    const org = { sid: "LIBERASTRA", name: "Liberastra" };
    vi.mocked(apiGet).mockImplementation(async path => {
      if (path === "/api/organizations/LIBERA") throw new ApiError(404, { title: "Not Found" });
      return { items: [org] };
    });
    expect(await searchGuildOrgsAction("Libera")).toEqual([org]);
  });

  it("keeps an exact result when the slower broad search fails", async () => {
    vi.mocked(apiGet).mockImplementation(async path => {
      if (path === "/api/organizations/X") return { sid: "X", name: "Corpo X" };
      throw new Error("API timeout");
    });
    expect(await searchGuildOrgsAction("X")).toEqual([{ sid: "X", name: "Corpo X" }]);
  });

  it("does not report API failure as an empty search result", async () => {
    vi.mocked(apiGet).mockRejectedValue(new Error("private upstream failure"));
    await expect(searchGuildOrgsAction("Missing")).rejects.toThrow("Recherche de corpos indisponible.");
  });

  it("makes no API request for invalid input or an unsigned user", async () => {
    expect(await searchGuildOrgsAction({ query: "X" })).toEqual([]);
    expect(await searchGuildOrgsAction(" ")).toEqual([]);
    expect(await searchGuildOrgsAction("x".repeat(101))).toEqual([]);
    vi.mocked(getSession).mockResolvedValue(null);
    await expect(searchGuildOrgsAction("X")).rejects.toThrow("Non authentifié.");
    expectNoApiCall();
    expect(revalidatePath).not.toHaveBeenCalled();
  });
});

describe("failures are answers, never exceptions", () => {
  it("refuses an anonymous caller", async () => {
    vi.mocked(getSession).mockResolvedValue(null);

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: false, error: "Non authentifié." });
    expectNoApiCall();
  });

  it("checks the arguments before looking at the session", async () => {
    vi.mocked(getSession).mockResolvedValue(null);

    expect(await mapGuildOrgAction("../x", "CORP")).toEqual({ ok: false, error: INVALID_ARGUMENTS });
    expect(getSession).not.toHaveBeenCalled();
  });

  it("falls back to the status title when the API gives no detail", async () => {
    vi.mocked(apiPost).mockRejectedValue(new ApiError(404, { title: "Not Found", status: 404 }));

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: false, error: "Not Found" });
  });

  it("gives a French message for anything that is not an Error", async () => {
    vi.mocked(apiPost).mockRejectedValue("boom");

    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({
      ok: false,
      error: "Échec de la validation du lien.",
    });
  });

  it("keeps transport and session implementation details private", async () => {
    vi.mocked(apiPost).mockRejectedValue(new Error("connect failed to private-api.internal:5000"));
    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: false, error: "Échec de la validation du lien." });
    vi.mocked(getSession).mockRejectedValueOnce(new Error("internal session failure"));
    expect(await acceptSuggestionAction(USER, 42, "Pilote42")).toEqual({ ok: false, error: "Échec de la validation du lien." });
  });
});
