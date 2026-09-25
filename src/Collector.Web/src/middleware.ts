import type { NextRequest, NextResponse } from "next/server";
import { verifyAccessToken } from "@/lib/auth/jwt";
import { authenticateRequest, type RefreshedTokens } from "@/lib/auth/request-auth";
import { buildCsp, newNonce } from "@/lib/security/csp";

/**
 * Middleware racine Next.js.
 *
 * Le site est PRIVÉ : toute page hors /login exige un access token dont la
 * signature RS256 est vérifiée ici (clé publique de l'API). Les tokens expirés
 * sont renouvelés avec le refresh token ; voir `authenticateRequest`.
 */

const API_BASE = process.env.API_BASE_URL ?? "http://127.0.0.1:5000";

async function refreshWithApi(
  refreshToken: string,
  clientIp?: string,
): Promise<RefreshedTokens | null> {
  const res = await fetch(`${API_BASE}/api/auth/refresh`, {
    method: "POST",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
      ...(clientIp ? { "X-Forwarded-For": clientIp } : {}),
    },
    body: JSON.stringify({ refreshToken }),
    cache: "no-store",
  });
  if (!res.ok) return null;
  const body = (await res.json()) as Partial<RefreshedTokens>;
  if (!body.accessToken || !body.refreshToken || !body.expiresAt) return null;
  return {
    accessToken: body.accessToken,
    refreshToken: body.refreshToken,
    expiresAt: body.expiresAt,
  };
}

// Report-Only first: violations are reported by browsers without breaking pages.
// Switch to "content-security-policy" once the console stays clean.
const CSP_HEADER = "content-security-policy-report-only";

export async function middleware(req: NextRequest): Promise<NextResponse> {
  const csp = buildCsp(newNonce(), { dev: process.env.NODE_ENV !== "production" });
  const res = await authenticateRequest(
    req,
    { verify: (token) => verifyAccessToken(token), refresh: refreshWithApi },
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
