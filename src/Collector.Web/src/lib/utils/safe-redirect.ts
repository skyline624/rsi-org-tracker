const FALLBACK = "/dashboard";
const PROBE_ORIGIN = "http://internal.invalid";

/**
 * Normalise un paramètre de redirection (`?from=`) en chemin interne.
 *
 * Refuse tout ce qui pourrait sortir du site : URL absolues, schémas
 * (`javascript:`), chemins protocol-relative (`//evil`, `/\evil`) et
 * variantes encodées. Retourne `/dashboard` dans ces cas.
 */
export function safeInternalPath(from: string | null | undefined): string {
  if (!from || !from.startsWith("/")) return FALLBACK;

  let decoded: string;
  try {
    decoded = decodeURIComponent(from);
  } catch {
    return FALLBACK;
  }
  if (decoded.startsWith("//") || decoded.startsWith("/\\")) return FALLBACK;

  const url = new URL(from, PROBE_ORIGIN);
  if (url.origin !== PROBE_ORIGIN) return FALLBACK;

  return `${url.pathname}${url.search}${url.hash}`;
}
