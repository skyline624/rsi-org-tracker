import { describe, expect, it } from "vitest";
import { lastChecked } from "./last-checked";

describe("lastChecked", () => {
  it("is the latest time the collector looked at the org, not its last change", () => {
    expect(
      lastChecked({
        timestamp: "2026-05-01T00:00:00Z",
        contentCheckedAt: "2026-09-24T08:00:00Z",
        membersCollectedAt: "2026-09-25T06:30:00Z",
      }),
    ).toBe("2026-09-25T06:30:00Z");
  });

  it("falls back to the snapshot time when the org was never checked since", () => {
    expect(
      lastChecked({ timestamp: "2026-05-01T00:00:00Z", contentCheckedAt: null }),
    ).toBe("2026-05-01T00:00:00Z");
  });
});
