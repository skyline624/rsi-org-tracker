/** Page number from a search param: a whole number >= 1, else 1. */
export function parsePage(raw: string | undefined): number {
  const page = Math.trunc(Number(raw));
  return Number.isFinite(page) && page >= 1 ? page : 1;
}

/**
 * The query string with `param` set to `page` (removed for page 1), other params
 * kept: several paginated lists can share a URL, each with its own param.
 */
export function withPage(search: string, param: string, page: number): string {
  const next = new URLSearchParams(search);
  if (page <= 1) next.delete(param);
  else next.set(param, String(page));
  return next.toString();
}
