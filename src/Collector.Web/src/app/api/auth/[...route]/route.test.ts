import { afterEach, describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { GET, POST } from "./route";

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

  // A link or an image on another site used to log the user out.
  it("refuses logout over GET, leaving the session alone", async () => {
    stubUpstream(200, {});
    const req = new NextRequest("http://localhost/api/auth/logout", {
      headers: { cookie: "sct_access=a; sct_refresh=r" },
    });

    const res = await GET(req, { params: Promise.resolve({ route: ["logout"] }) });

    expect(res.status).toBe(405);
    expect(res.headers.get("set-cookie")).toBeNull();
  });

  it.each(["logout", "login", "refresh"])(
    "refuses a %s posted from another site (a hidden form)",
    async (segment) => {
      stubUpstream(200, auth);
      const req = new NextRequest(`http://localhost/api/auth/${segment}`, {
        method: "POST",
        body: "{}",
        headers: { origin: "https://evil.example", host: "localhost" },
      });

      const res = await POST(req, { params: Promise.resolve({ route: [segment] }) });

      expect(res.status).toBe(403);
      expect(res.headers.get("set-cookie")).toBeNull();
    },
  );

  it("accepts a logout posted from the site itself", async () => {
    stubUpstream(200, {});
    const req = new NextRequest("http://localhost/api/auth/logout", {
      method: "POST",
      headers: { origin: "http://localhost", host: "localhost", cookie: "sct_refresh=r" },
    });

    const res = await POST(req, { params: Promise.resolve({ route: ["logout"] }) });

    expect(res.status).toBe(303);
    expect(res.headers.get("set-cookie")).toMatch(/sct_refresh=;/);
  });

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

  it("forwards the client IP set by nginx to the API (login rate limit, audit log)", async () => {
    stubUpstream(200, auth);
    const req = new NextRequest("http://localhost/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ username: "u", password: "p" }),
      headers: { "x-forwarded-for": "203.0.113.7" },
    });

    await POST(req, { params: Promise.resolve({ route: ["login"] }) });

    const init = vi.mocked(fetch).mock.calls[0]![1]!;
    expect((init.headers as Record<string, string>)["X-Forwarded-For"]).toBe("203.0.113.7");
  });

  it("logout clears the cookies and sends the browser back to /login", async () => {
    stubUpstream(200, { message: "Logged out" });

    const res = await call("logout");

    expect(res.status).toBe(303);
    expect(res.headers.get("location")).toBe("/login");
    expect(res.headers.get("set-cookie")).toMatch(/sct_access=;/);
    expect(res.headers.get("set-cookie")).toMatch(/sct_refresh=;/);
  });

  it.each(["register", "forgot-password", "reset-password"])(
    "no longer relays the removed %s flow",
    async (segment) => {
      stubUpstream(200, {});

      const res = await call(segment, "{}");

      expect(res.status).toBe(404);
      expect(fetch).not.toHaveBeenCalled();
    },
  );

  it("marks every auth response as non-cacheable", async () => {
    stubUpstream(401, { title: "Invalid credentials" });

    const res = await call("login", JSON.stringify({ username: "u", password: "bad" }));

    expect(res.status).toBe(401);
    expect(res.headers.get("cache-control")).toContain("no-store");
  });
});
