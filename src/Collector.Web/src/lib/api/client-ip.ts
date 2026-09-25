import { isIP } from "node:net";

/**
 * Client address from an X-Forwarded-For header. nginx overwrites it with the
 * real peer address; only the first entry is kept, and only if it is an IP.
 */
export function firstForwardedIp(header: string | null | undefined): string | undefined {
  const first = header?.split(",")[0]?.trim();
  return first && isIP(first) ? first : undefined;
}
