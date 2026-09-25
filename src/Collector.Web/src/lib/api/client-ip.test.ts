import { describe, expect, it } from "vitest";
import { firstForwardedIp } from "./client-ip";

describe("firstForwardedIp", () => {
  it.each([
    ["203.0.113.7", "203.0.113.7"],
    ["203.0.113.7, 10.0.0.1", "203.0.113.7"],
    [" 2001:db8::1 ", "2001:db8::1"],
  ])("keeps the client address from %j", (header, expected) => {
    expect(firstForwardedIp(header)).toBe(expected);
  });

  it.each([null, "", "not-an-ip", "203.0.113.7\r\nX-Evil: 1", "<script>"])(
    "ignores %j",
    (header) => {
      expect(firstForwardedIp(header)).toBeUndefined();
    },
  );
});
