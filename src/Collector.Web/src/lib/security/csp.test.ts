import { describe, expect, it } from "vitest";
import { buildCsp, newNonce } from "./csp";

function directive(csp: string, name: string): string {
  return csp.split(";").map((d) => d.trim()).find((d) => d.startsWith(`${name} `)) ?? "";
}

describe("Content-Security-Policy", () => {
  const csp = buildCsp("abc123", { dev: false });

  it("only runs scripts carrying the request nonce", () => {
    const scripts = directive(csp, "script-src");
    expect(scripts).toContain("'nonce-abc123'");
    expect(scripts).toContain("'strict-dynamic'");
    expect(scripts).not.toContain("'unsafe-inline'");
    expect(scripts).not.toContain("'unsafe-eval'");
  });

  it("cannot be framed and loads no plugins", () => {
    expect(directive(csp, "frame-ancestors")).toBe("frame-ancestors 'none'");
    expect(directive(csp, "object-src")).toBe("object-src 'none'");
    expect(directive(csp, "base-uri")).toBe("base-uri 'self'");
  });

  it("lets the browser reach UEX, whose Cloudflare blocks our server", () => {
    expect(directive(csp, "connect-src")).toBe("connect-src 'self' https://api.uexcorp.space");
  });

  it("allows eval only in development (React refresh)", () => {
    expect(directive(buildCsp("n", { dev: true }), "script-src")).toContain("'unsafe-eval'");
  });

  it("draws a fresh, unguessable nonce each time", () => {
    const a = newNonce();
    expect(a).toMatch(/^[A-Za-z0-9+/]{22,}={0,2}$/);
    expect(newNonce()).not.toBe(a);
  });
});
