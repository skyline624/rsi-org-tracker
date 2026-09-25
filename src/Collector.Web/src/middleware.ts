import type { NextRequest, NextResponse } from "next/server";
import { refreshWithApi } from "@/lib/auth/api-refresh";
import { verifyAccessToken } from "@/lib/auth/jwt";
import { authenticateRequest } from "@/lib/auth/request-auth";
import { buildCsp, newNonce } from "@/lib/security/csp";

/**
 * Middleware racine Next.js.
 *
 * Le site est PRIVÉ : toute page hors /login exige un access token dont la
 * signature RS256 est vérifiée ici (clé publique de l'API). Les tokens expirés
 * sont renouvelés avec le refresh token ; voir `authenticateRequest`.
 */

// Report-Only first: violations are reported by browsers without breaking pages.
// Switch to "content-security-policy" once the console stays clean.
const CSP_HEADER = "content-security-policy-report-only";

export async function middleware(req: NextRequest): Promise<NextResponse> {
  const csp = buildCsp(newNonce(), { dev: process.env.NODE_ENV !== "production" });
  const res = await authenticateRequest(
    req,
    { verify: (token) => verifyAccessToken(token), refresh: (rt, ip) => refreshWithApi(rt, ip) },
    // Next.js reads the nonce from this request header and stamps it on its scripts.
    { [CSP_HEADER]: csp },
  );
  res.headers.set(CSP_HEADER, csp);
  return res;
}

export const config = {
  // Node.js runtime: reads API_BASE_URL at run time and shares the JWKS cache.
  runtime: "nodejs",
  // Exclure tous les assets statiques et les routes API propres à Next
  matcher: [
    "/((?!_next/static|_next/image|favicon.ico|fonts/|textures/|api/auth).*)",
  ],
};
