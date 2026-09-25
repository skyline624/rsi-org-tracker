import { describe, expect, it } from "vitest";
import { accessCookieOptions, refreshCookieOptions } from "./cookies";

describe("auth cookie options", () => {
  // Lax, not Strict: a link opened from Discord or another site must reach the page
  // with the session. Cross-site POSTs are still refused (Lax) and Server Actions
  // check the Origin header.
  it("sends the session on top-level navigation from other sites", () => {
    expect(accessCookieOptions(new Date()).sameSite).toBe("lax");
    expect(refreshCookieOptions().sameSite).toBe("lax");
  });

  it("keeps both cookies out of reach of page scripts", () => {
    expect(accessCookieOptions(new Date()).httpOnly).toBe(true);
    expect(refreshCookieOptions().httpOnly).toBe(true);
  });
});
