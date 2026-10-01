import { describe, expect, it } from "vitest";

import { normalizeFingerprint } from "../scTracker.desktop/lib/fingerprint";

// 32 bytes, as the site shows it (the value of COLLECTOR_API_Discord__Ingest__CertificateSha256).
const COLONS = "0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0:0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0";
const HEX = COLONS.replaceAll(":", "");

describe("normalizeFingerprint", () => {
  it.each([
    ["the colon-separated form the site shows", COLONS],
    ["the whole openssl 3 line", `sha256 Fingerprint=${COLONS}`],
    ["the whole openssl 1.1 line", `SHA256 Fingerprint=${COLONS}`],
    ["the openssl line with its trailing newline", `sha256 Fingerprint=${COLONS}\n`],
    ["lower case", COLONS.toLowerCase()],
    ["bare hex", HEX],
    ["bytes separated by spaces", COLONS.replaceAll(":", " ")],
    ["surrounding blanks", `  ${HEX}\t`],
  ])("accepts %s", (_label, input) => {
    expect(normalizeFingerprint(input)).toBe(HEX);
  });

  it.each([
    ["63 hex characters", HEX.slice(1)],
    ["65 hex characters", `${HEX}A`],
    ["a non-hex character", `G${HEX.slice(1)}`],
    ["an empty string", ""],
    ["a SHA-1 fingerprint (20 bytes)", HEX.slice(0, 40)],
    ["another digest's openssl line", `sha1 Fingerprint=${COLONS}`],
    ["a dash separator", COLONS.replaceAll(":", "-")],
  ])("refuses %s", (_label, input) => {
    expect(normalizeFingerprint(input)).toBeNull();
  });

  it("returns null instead of throwing when the setting is not a string", () => {
    expect(normalizeFingerprint(undefined as unknown as string)).toBeNull();
    expect(normalizeFingerprint(42 as unknown as string)).toBeNull();
  });
});
