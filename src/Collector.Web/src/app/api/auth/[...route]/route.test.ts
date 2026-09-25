import { afterEach, describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { POST } from "./route";

const auth = {
  accessToken: "header.payload.signature",
  refreshToken: "opaque-refresh-token",
  expiresAt: "2030-01-01T00:00:00Z",
  user: { id: 1, username: "pilot" },
};

function stubUpstream(status: number, body: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async () =>
        new Response(JSON.stringify(body), {
          status,
          headers: { "content-type": "application/json" },
        }),
    ),
  );
}

function call(segment: string, body = "") {
  const req = new NextRequest(`http://localhost/api/auth/${segment}`, {
    method: "POST",
    body,
  });
  return POST(req, { params: Promise.resolve({ route: [segment] }) });
}

describe("BFF /api/auth", () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(["login", "refresh"])(
    "%s keeps the tokens in httpOnly cookies and out of the response body",
    async (segment) => {
      stubUpstream(200, auth);

      const res = await call(segment, JSON.stringify({ username: "u", password: "p" }));
      const body = await res.json();

      expect(body).toEqual({ user: auth.user, expiresAt: auth.expiresAt });
      expect(JSON.stringify(body)).not.toContain(auth.refreshToken);
      expect(res.cookies.get("sct_access")?.value).toBe(auth.accessToken);
      expect(res.cookies.get("sct_refresh")?.value).toBe(auth.refreshToken);
    },
  );

  it("logout clears the cookies and sends the browser back to /login", async () => {
    stubUpstream(200, { message: "Logged out" });

    const res = await call("logout");

    expect(res.status).toBe(303);
    expect(res.headers.get("location")).toBe("/login");
    expect(res.headers.get("set-cookie")).toMatch(/sct_access=;/);
    expect(res.headers.get("set-cookie")).toMatch(/sct_refresh=;/);
  });

  it("marks every auth response as non-cacheable", async () => {
    stubUpstream(401, { title: "Invalid credentials" });

    const res = await call("login", JSON.stringify({ username: "u", password: "bad" }));

    expect(res.status).toBe(401);
    expect(res.headers.get("cache-control")).toContain("no-store");
  });
});
