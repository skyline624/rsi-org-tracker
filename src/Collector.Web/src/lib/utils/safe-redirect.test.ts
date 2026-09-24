import { describe, expect, it } from "vitest";
import { safeInternalPath } from "./safe-redirect";

describe("safeInternalPath", () => {
  it.each([
    ["/orgs", "/orgs"],
    ["/orgs/OPPF?tab=members", "/orgs/OPPF?tab=members"],
    ["/users/L66#notes", "/users/L66#notes"],
  ])("keeps internal path %s", (input, expected) => {
    expect(safeInternalPath(input)).toBe(expected);
  });

  it.each([
    "//evil.example",
    "/\\evil.example",
    "https://evil.example/x",
    "javascript:alert(1)",
    "evil.example",
    "",
    "/%2F%2Fevil.example",
  ])("rejects %s and falls back to /dashboard", (input) => {
    expect(safeInternalPath(input)).toBe("/dashboard");
  });

  it("falls back when the parameter is missing", () => {
    expect(safeInternalPath(null)).toBe("/dashboard");
  });
});
