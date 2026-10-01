import { describe, expect, it } from "vitest";

import { type CollectionStopReason, computeCoverage } from "../scTracker.desktop/lib/coverage";

describe("computeCoverage", () => {
  it.each(["cursor_stalled", "invalid_cursor", "call_cap", "deadline", "error", "aborted"] satisfies CollectionStopReason[])(
    "stays partial after %s even when the counts match", reason => {
      expect(computeCoverage("member-search", 1000, 1000, reason).complete).toBe(false);
    });

  it("refuses invalid counts as evidence of completeness", () => {
    expect(computeCoverage("member-search", Infinity, Infinity, "exhausted").complete).toBe(false);
    expect(computeCoverage("member-search", 1.5, 1.5, "exhausted").complete).toBe(false);
  });

  it("is complete when member search collected exactly the expected count", () => {
    expect(computeCoverage("member-search", 1234, 1234, "exhausted"))
      .toEqual({ method: "member-search", complete: true, expectedCount: 1234, collectedCount: 1234 });
  });

  it.each([
    ["fewer members than expected", 1233, 1234],
    ["more members than expected", 1235, 1234],
    ["no expected count", 1234, null],
    ["an empty result", 0, 0],
  ])("is partial for member search with %s", (_label, collected, expected) => {
    expect(computeCoverage("member-search", collected, expected, "exhausted"))
      .toEqual({ method: "member-search", complete: false, expectedCount: expected, collectedCount: collected });
  });

  it.each(["role-members", "cache"] as const)("is never complete for %s", method => {
    expect(computeCoverage(method, 50, 50, "exhausted").complete).toBe(false);
    expect(computeCoverage(method, 50, null, "exhausted"))
      .toEqual({ method, complete: false, expectedCount: null, collectedCount: 50 });
  });
});
