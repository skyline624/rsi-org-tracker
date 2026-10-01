/**
 * The line `openssl x509 -noout -fingerprint -sha256` prints: "sha256 Fingerprint=" (openssl 3)
 * or "SHA256 Fingerprint=" (openssl 1.1), then the colon-separated bytes.
 */
const OPENSSL_PREFIX = /^\s*sha256\s+fingerprint\s*=/i;

/**
 * Normalises a SHA-256 certificate fingerprint to 64 upper-case hex characters, or returns
 * null when it is not one. Users paste it from the site (colon-separated bytes) or straight
 * from openssl, so the "sha256 Fingerprint=" prefix, the colons, the blanks and the case are
 * ignored. Node's `getPeerCertificate().fingerprint256` goes through the same function, so the
 * pin compares two values of the same form.
 */
export function normalizeFingerprint(input: string): string | null {
  if (typeof input !== "string") return null;
  const hex = input.replace(OPENSSL_PREFIX, "").replace(/[:\s]/g, "").toUpperCase();
  return /^[0-9A-F]{64}$/.test(hex) ? hex : null;
}
