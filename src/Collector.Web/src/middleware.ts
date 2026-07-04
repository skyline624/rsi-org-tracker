import { NextRequest, NextResponse } from "next/server";
import { COOKIE_ACCESS } from "@/lib/auth/cookies";

/**
 * Middleware racine Next.js.
 *
 * Le site est PRIVÉ : toutes les pages exigent un cookie `sct_access`, à
 * l'exception des pages d'authentification publiques ci-dessous. On ne vérifie
 * ici que la présence du cookie — l'API re-valide la signature du token, et les
 * rôles (admin) sont appliqués côté API.
 */

// Seules ces routes sont accessibles sans compte.
const PUBLIC_PREFIXES = ["/login", "/forgot-password", "/reset-password"];

export function middleware(req: NextRequest): NextResponse {
  const { pathname } = req.nextUrl;

  const isPublic = PUBLIC_PREFIXES.some(
    (p) => pathname === p || pathname.startsWith(`${p}/`),
  );
  if (isPublic) return NextResponse.next();

  if (req.cookies.has(COOKIE_ACCESS)) return NextResponse.next();

  // Redirect vers /login avec le "from" pour revenir après.
  // Derrière nginx, req.url porte l'hôte interne (localhost:3000) ; on reconstruit
  // l'URL à partir du Host / X-Forwarded-Proto transmis par le proxy pour que le
  // navigateur atterrisse sur l'URL publique.
  const host =
    req.headers.get("x-forwarded-host") ?? req.headers.get("host") ?? req.nextUrl.host;
  const proto = req.headers.get("x-forwarded-proto") ?? "https";
  const loginUrl = new URL(`${proto}://${host}/login`);
  loginUrl.searchParams.set("from", pathname);
  return NextResponse.redirect(loginUrl);
}

export const config = {
  // Exclure tous les assets statiques et les routes API propres à Next
  matcher: [
    "/((?!_next/static|_next/image|favicon.ico|fonts/|textures/|api/auth).*)",
  ],
};
