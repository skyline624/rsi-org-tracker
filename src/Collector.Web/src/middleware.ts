import type { NextRequest, NextResponse } from "next/server";
import { verifyAccessToken } from "@/lib/auth/jwt";
import { authenticateRequest, type RefreshedTokens } from "@/lib/auth/request-auth";

/**
 * Middleware racine Next.js.
 *
 * Le site est PRIVÉ : toute page hors /login exige un access token dont la
 * signature RS256 est vérifiée ici (clé publique de l'API). Les tokens expirés
 * sont renouvelés avec le refresh token ; voir `authenticateRequest`.
 */

const API_BASE = process.env.API_BASE_URL ?? "http://127.0.0.1:5000";

async function refreshWithApi(refreshToken: string): Promise<RefreshedTokens | null> {
  const res = await fetch(`${API_BASE}/api/auth/refresh`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
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

export function middleware(req: NextRequest): Promise<NextResponse> {
  return authenticateRequest(req, {
    verify: (token) => verifyAccessToken(token),
    refresh: refreshWithApi,
  });
}

export const config = {
  // Node.js runtime: reads API_BASE_URL at run time and shares the JWKS cache.
  runtime: "nodejs",
  // Exclure tous les assets statiques et les routes API propres à Next
  matcher: [
    "/((?!_next/static|_next/image|favicon.ico|fonts/|textures/|api/auth).*)",
  ],
};
