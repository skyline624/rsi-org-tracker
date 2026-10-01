import { afterEach, describe, expect, it, vi } from "vitest";
import {
  getDiscordDiscrepancies,
  getDiscordEvents,
  getDiscordGuild,
  getDiscordIngestConfig,
  getDiscordMembers,
  getDiscordMulti,
  getDiscordSuggestions,
  getDiscordSyncs,
  getOrgDiscordGuilds,
  getOrgMembersPage,
  getUserDiscord,
  listApiKeys,
  listDiscordGuilds,
} from "./endpoints";

describe("getOrgMembersPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("asks the API for one page of the roster, by status", async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ items: [], total: 0, page: 3, pageSize: 50, totalPages: 0 }), { status: 200 }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await getOrgMembersPage("TEST", { status: "former", page: 3, pageSize: 50 }, { bearerToken: "t" });

    const url = new URL(String((fetchMock.mock.calls[0] as unknown[])[0]));
    expect(url.pathname).toBe("/api/organizations/TEST/members");
    expect(Object.fromEntries(url.searchParams)).toEqual({ status: "former", page: "3", pageSize: "50" });
  });
});


describe("settings endpoints", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubFetch(body: unknown) {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  function firstCall(fetchMock: ReturnType<typeof stubFetch>) {
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    return { url: new URL(url), init };
  }

  it("lists the user's API keys with the user's token", async () => {
    const fetchMock = stubFetch([]);

    await listApiKeys({ bearerToken: "t" });

    const { url, init } = firstCall(fetchMock);
    expect(url.pathname).toBe("/api/api-keys");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer t");
  });

  it("reads the plugin settings from the Discord ingest config", async () => {
    const fetchMock = stubFetch({ publicUrl: null, certificateSha256: null });

    const config = await getDiscordIngestConfig({ bearerToken: "t" });

    expect(firstCall(fetchMock).url.pathname).toBe("/api/discord/ingest-config");
    expect(config).toEqual({ publicUrl: null, certificateSha256: null });
  });
});


describe("Discord roster endpoints", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const ctx = { bearerToken: "t" };
  const guildId = "123456789012345678";

  function stub(body: unknown) {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  function urls(fetchMock: ReturnType<typeof stub>) {
    return fetchMock.mock.calls.map((call) => new URL(String((call as unknown[])[0])));
  }

  it.each([
    ["the server list", () => listDiscordGuilds(ctx), "/api/discord/guilds"],
    ["a server", () => getDiscordGuild(ctx, guildId), `/api/discord/guilds/${guildId}`],
    ["its RSI discrepancies", () => getDiscordDiscrepancies(ctx, guildId), `/api/discord/guilds/${guildId}/discrepancies`],
    ["its link suggestions", () => getDiscordSuggestions(ctx, guildId), `/api/discord/guilds/${guildId}/suggestions`],
    ["a citizen's cross profile", () => getUserDiscord(ctx, "Pilote42"), "/api/users/Pilote42/discord"],
    ["an org's servers", () => getOrgDiscordGuilds(ctx, "CORP"), "/api/organizations/CORP/discord"],
  ])("reads %s with the user's token", async (_, call, path) => {
    const fetchMock = stub([]);

    await call();

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(new URL(url).pathname).toBe(path);
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer t");
  });

  it("encodes every path segment it is given", async () => {
    const fetchMock = stub({});

    await getDiscordGuild(ctx, "1/2?x");
    await getUserDiscord(ctx, "a b#c");
    await getOrgDiscordGuilds(ctx, "A/B");

    const called = urls(fetchMock);
    expect(called.map((u) => u.pathname)).toEqual([
      "/api/discord/guilds/1%2F2%3Fx",
      "/api/users/a%20b%23c/discord",
      "/api/organizations/A%2FB/discord",
    ]);
    expect(called.every((u) => u.search === "")).toBe(true);
  });

  it("asks the API for one page of members, with the page's filters", async () => {
    const fetchMock = stub({ items: [], total: 0, page: 2, pageSize: 50, totalPages: 0 });

    await getDiscordMembers(ctx, guildId, {
      status: "former",
      search: "pilote",
      rankRoleId: "223456789012345678",
      reconciliation: "rank_mismatch",
      page: 2,
      pageSize: 50,
    });

    const [url] = urls(fetchMock);
    expect(url?.pathname).toBe(`/api/discord/guilds/${guildId}/members`);
    expect(Object.fromEntries(url?.searchParams ?? [])).toEqual({
      status: "former",
      search: "pilote",
      rankRoleId: "223456789012345678",
      reconciliation: "rank_mismatch",
      page: "2",
      pageSize: "50",
    });
  });

  it("leaves unset member filters out of the query", async () => {
    const fetchMock = stub({ items: [], total: 0, page: 1, pageSize: 50, totalPages: 0 });

    await getDiscordMembers(ctx, guildId, { status: "active", search: undefined, page: 1, pageSize: 50 });

    expect(Object.fromEntries(urls(fetchMock)[0]?.searchParams ?? [])).toEqual({
      status: "active",
      page: "1",
      pageSize: "50",
    });
  });

  it("bounds the history, the upload journal and the multi-membership page", async () => {
    const fetchMock = stub([]);

    await getDiscordEvents(ctx, guildId, { type: "left", userId: "323456789012345678", limit: 100 });
    await getDiscordSyncs(ctx, guildId, 20);
    await getDiscordMulti(ctx, { page: 3, pageSize: 50 });

    expect(urls(fetchMock).map((u) => [u.pathname, Object.fromEntries(u.searchParams)])).toEqual([
      [`/api/discord/guilds/${guildId}/events`, { type: "left", userId: "323456789012345678", limit: "100" }],
      [`/api/discord/guilds/${guildId}/syncs`, { limit: "20" }],
      ["/api/discord/multi", { page: "3", pageSize: "50" }],
    ]);
  });
});
