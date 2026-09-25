import { afterEach, describe, expect, it, vi } from "vitest";
import { getOrgMembersPage } from "./endpoints";

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
