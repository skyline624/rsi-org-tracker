/**
 * Content-Security-Policy with a per-request nonce (built in the middleware).
 *
 * Scripts must carry the nonce Next.js stamps on its own tags ('strict-dynamic'
 * lets them load their chunks). Styles keep 'unsafe-inline' (Tailwind / framer-motion
 * inline styles); images come from the RSI, Discord and UEX CDNs.
 */

export function newNonce(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Buffer.from(bytes).toString("base64");
}

export function buildCsp(nonce: string, { dev }: { dev: boolean }): string {
  const scriptSrc = ["'self'", `'nonce-${nonce}'`, "'strict-dynamic'", ...(dev ? ["'unsafe-eval'"] : [])];
  return [
    "default-src 'self'",
    `script-src ${scriptSrc.join(" ")}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob: https:",
    "font-src 'self' data:",
    "media-src 'self' blob:",
    // The UEX panel queries UEX from the browser: its Cloudflare blocks our server.
    "connect-src 'self' https://api.uexcorp.space",
    "frame-ancestors 'none'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
  ].join("; ");
}
