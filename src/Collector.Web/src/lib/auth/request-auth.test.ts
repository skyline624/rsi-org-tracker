import { describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import type { JWTPayload } from "jose";
import {
  authenticateRequest,
  type AuthDeps,
  type RefreshOutcome,
  type RefreshedTokens,
} from "./request-auth";

const nowSec = () => Math.floor(Date.now() / 1000);

function request(path: string, cookies: Record<string, string> = {}) {
  const cookie = Object.entries(cookies)
    .map(([k, v]) => `${k}=${v}`)
    .join("; ");
  return new NextRequest(`http://127.0.0.1:3000${path}`, {
    headers: {
      host: "tracker.example",
      "x-forwarded-proto": "https",
      ...(cookie ? { cookie } : {}),
    },
  });
}

function deps(
  tokens: Record<string, JWTPayload>,
  refreshed: RefreshedTokens | "rejected" | "unavailable" = "rejected",
): AuthDeps & { refresh: ReturnType<typeof vi.fn> } {
  const outcome: RefreshOutcome =
    typeof refreshed === "string" ? { status: refreshed } : { status: "ok", tokens: refreshed };
  return {
    verify: vi.fn(async (t: string) => tokens[t] ?? null),
    refresh: vi.fn(async () => outcome),
  };
}

const passedThrough = (res: Response) => res.headers.get("x-middleware-next") === "1";
const setCookie = (res: Response) => res.headers.get("set-cookie") ?? "";

describe("authenticateRequest", () => {
  it("lets public pages through without a session", async () => {
    const res = await authenticateRequest(request("/login"), deps({}));

    expect(passedThrough(res)).toBe(true);
  });

  it("treats the removed password-reset pages like any private page", async () => {
    const res = await authenticateRequest(request("/forgot-password"), deps({}));

    expect(res.status).toBe(307);
  });

  it("hands extra request headers (CSP nonce) to the page on every pass-through", async () => {
    const extra = { "content-security-policy-report-only": "script-src 'nonce-n1'" };
    const passes = [
      await authenticateRequest(request("/login"), deps({}), extra),
      await authenticateRequest(
        request("/orgs", { sct_access: "good" }),
        deps({ good: { sub: "1", exp: nowSec() + 600 } }),
        extra,
      ),
      await authenticateRequest(
        request("/orgs", { sct_refresh: "rt-csp" }),
        deps({}, { accessToken: "a", refreshToken: "r", expiresAt: new Date(Date.now() + 900_000).toISOString() }),
        extra,
      ),
    ];

    for (const res of passes) {
      expect(res.headers.get("x-middleware-request-content-security-policy-report-only")).toBe(
        "script-src 'nonce-n1'",
      );
    }
  });

  it("redirects to the public /login URL when there is no session", async () => {
    const res = await authenticateRequest(request("/orgs/OPPF"), deps({}));

    expect(res.status).toBe(307);
    expect(res.headers.get("location")).toBe(
      "https://tracker.example/login?from=%2Forgs%2FOPPF",
    );
  });

  it("keeps the request's own scheme when no proxy sets X-Forwarded-Proto (local run)", async () => {
    const req = new NextRequest("http://127.0.0.1:3000/orgs", { headers: { host: "127.0.0.1:3000" } });

    const res = await authenticateRequest(req, deps({}));

    expect(res.headers.get("location")).toBe("http://127.0.0.1:3000/login?from=%2Forgs");
  });

  it("ignores a client-supplied X-Forwarded-Host when building the redirect", async () => {
    const req = request("/orgs");
    req.headers.set("x-forwarded-host", "evil.example");

    const res = await authenticateRequest(req, deps({}));

    expect(res.headers.get("location")).toMatch(/^https:\/\/tracker\.example\//);
  });

  it("rejects a forged access cookie and clears the auth cookies", async () => {
    const res = await authenticateRequest(
      request("/orgs", { sct_access: "forged" }),
      deps({}),
    );

    expect(res.status).toBe(307);
    expect(setCookie(res)).toMatch(/sct_access=;/);
  });

  it("lets a verified, non-expiring access token through without refreshing", async () => {
    const d = deps({ good: { sub: "1", exp: nowSec() + 600 } });

    const res = await authenticateRequest(
      request("/orgs", { sct_access: "good", sct_refresh: "rt" }),
      d,
    );

    expect(passedThrough(res)).toBe(true);
    expect(d.refresh).not.toHaveBeenCalled();
  });

  it("refreshes an invalid access token and hands the new one to the page", async () => {
    const d = deps({}, {
      accessToken: "new-access",
      refreshToken: "new-refresh",
      expiresAt: new Date(Date.now() + 900_000).toISOString(),
    });

    const res = await authenticateRequest(
      request("/orgs", { sct_access: "expired", sct_refresh: "rt-1" }),
      d,
    );

    expect(passedThrough(res)).toBe(true);
    expect(setCookie(res)).toContain("sct_access=new-access");
    expect(setCookie(res)).toContain("sct_refresh=new-refresh");
    expect(res.headers.get("x-middleware-request-cookie")).toContain("sct_access=new-access");
  });

  it("forwards the client IP with the refresh call", async () => {
    const d = deps({});
    const req = request("/orgs", { sct_refresh: "rt-ip" });
    req.headers.set("x-forwarded-for", "203.0.113.7");

    await authenticateRequest(req, d);

    expect(d.refresh).toHaveBeenCalledWith("rt-ip", "203.0.113.7");
  });

  it("refreshes proactively when the access token expires within a minute", async () => {
    const d = deps({ soon: { sub: "1", exp: nowSec() + 20 } }, {
      accessToken: "new-access",
      refreshToken: "new-refresh",
      expiresAt: new Date(Date.now() + 900_000).toISOString(),
    });

    await authenticateRequest(request("/orgs", { sct_access: "soon", sct_refresh: "rt-2" }), d);

    expect(d.refresh).toHaveBeenCalledWith("rt-2", undefined);
  });

  it("redirects and clears cookies when the refresh is refused", async () => {
    const res = await authenticateRequest(
      request("/orgs", { sct_access: "expired", sct_refresh: "revoked" }),
      deps({}, "rejected"),
    );

    expect(res.status).toBe(307);
    expect(setCookie(res)).toMatch(/sct_refresh=;/);
  });

  it("keeps the session and answers 503 when the API cannot renew it (restart, deploy)", async () => {
    const res = await authenticateRequest(
      request("/orgs", { sct_access: "expired", sct_refresh: "rt-down" }),
      deps({}, "unavailable"),
    );

    expect(res.status).toBe(503);
    expect(res.headers.get("retry-after")).toBeTruthy();
    expect(res.headers.get("cache-control")).toBe("no-store");
    expect(setCookie(res)).toBe("");
  });

  it("still lets a token valid a few more seconds through when the API is unavailable", async () => {
    const res = await authenticateRequest(
      request("/orgs", { sct_access: "soon", sct_refresh: "rt-down-2" }),
      deps({ soon: { sub: "1", exp: nowSec() + 20 } }, "unavailable"),
    );

    expect(passedThrough(res)).toBe(true);
  });

  it("treats a refresh call that throws as the API being unavailable", async () => {
    const d = deps({});
    d.refresh.mockRejectedValue(new TypeError("fetch failed"));

    const res = await authenticateRequest(request("/orgs", { sct_refresh: "rt-throws" }), d);

    expect(res.status).toBe(503);
  });

  it("refreshes a given refresh token only once for concurrent requests", async () => {
    const d = deps({});
    let release: (v: unknown) => void = () => {};
    d.refresh.mockImplementation(
      () =>
        new Promise((resolve) => {
          release = () =>
            resolve({
              status: "ok",
              tokens: {
                accessToken: "a",
                refreshToken: "r",
                expiresAt: new Date(Date.now() + 900_000).toISOString(),
              },
            });
        }),
    );

    const first = authenticateRequest(request("/orgs", { sct_refresh: "rt-shared" }), d);
    const second = authenticateRequest(request("/users", { sct_refresh: "rt-shared" }), d);
    await vi.waitFor(() => expect(d.refresh).toHaveBeenCalled());
    release(undefined);
    await Promise.all([first, second]);

    expect(d.refresh).toHaveBeenCalledTimes(1);
  });
});
