import { afterEach, describe, expect, it, vi } from "vitest";
import type { DiscordTimelineEntryDto, DiscordUserProfileDto } from "@/lib/api/types";
import { loadUserDiscord } from "./user-profile";

const ctx = { bearerToken: "t" };
const GUILD = "123456789012345678";

const discordJoin: DiscordTimelineEntryDto = {
  source: "discord",
  type: "joined",
  at: "2025-03-14T20:11:05Z",
  notBefore: null,
  orgSid: "CORP",
  guildId: GUILD,
  guildName: "Ma Corpo",
  oldValue: null,
  newValue: "2025-03-14T20:11:05Z",
};

const rsiJoin: DiscordTimelineEntryDto = {
  source: "rsi",
  type: "member_joined",
  at: "2025-01-02T10:00:00Z",
  notBefore: null,
  orgSid: "CORP",
  guildId: null,
  guildName: null,
  oldValue: null,
  newValue: null,
};

const profile: DiscordUserProfileDto = {
  accounts: [
    {
      discordUserId: "323456789012345678",
      username: "pilote42",
      globalName: "Pilote",
      guilds: [
        {
          guildId: GUILD,
          guildName: "Ma Corpo",
          orgSid: "CORP",
          rank: "Officier",
          joinedAt: "2025-03-14T20:11:05Z",
          leftAt: null,
          lastSeenAt: "2026-09-30T12:00:00Z",
        },
      ],
    },
  ],
  timeline: [discordJoin, rsiJoin],
};

function stubFetch(status: number, body: unknown) {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), { status }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("loadUserDiscord", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("returns the cross profile of a citizen with linked accounts", async () => {
    const fetchMock = stubFetch(200, profile);

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "profile", profile });
    const [url] = fetchMock.mock.calls[0] as unknown as [string];
    expect(new URL(url).pathname).toBe("/api/users/Pilote42/discord");
  });

  it("gives the empty state for a citizen without Discord data (200 with empty lists)", async () => {
    stubFetch(200, { accounts: [], timeline: [] });

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "empty" });
  });

  it("gives the empty state when there are RSI entries but no linked account", async () => {
    stubFetch(200, { accounts: [], timeline: [rsiJoin] });

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "empty" });
  });

  it("gives the empty state for a handle the API does not know (404) instead of failing the page", async () => {
    stubFetch(404, { title: "Not Found", status: 404 });

    expect(await loadUserDiscord(ctx, "NoSuchCitizen")).toEqual({ kind: "empty" });
  });

  it("reports an API failure as unavailable instead of throwing", async () => {
    stubFetch(500, { title: "Internal Server Error", status: 500 });

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "unavailable" });
  });

  it("reports an unreachable API as unavailable", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new TypeError("fetch failed");
      }),
    );

    expect(await loadUserDiscord(ctx, "Pilote42")).toEqual({ kind: "unavailable" });
  });
});
