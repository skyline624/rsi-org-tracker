import { NextRequest, NextResponse } from "next/server";
import { COOKIE_ACCESS } from "@/lib/auth/cookies";

/**
 * Middleware racine Next.js.
 *
 * Rôle : bloquer l'accès aux routes "(user)" quand l'utilisateur n'a pas
 * de cookie `sct_access`. On ne vérifie PAS la signature ici — juste la
 * présence. Les accès effectifs à l'API re-valident le token.
 *
 * Les pages admin (v2) seraient à ajouter ici avec un décodage du JWT.
 */

const PROTECTED_PREFIXES = ["/dashboard", "/favorites", "/settings"];

export function middleware(req: NextRequest): NextResponse {
  const { pathname } = req.nextUrl;

  const needsAuth = PROTECTED_PREFIXES.some(
    (p) => pathname === p || pathname.startsWith(`${p}/`),
  );
  if (!needsAuth) return NextResponse.next();

  const hasAccess = req.cookies.has(COOKIE_ACCESS);
  if (hasAccess) return NextResponse.next();

  // Redirect vers /login avec le "from" pour revenir après
  const loginUrl = new URL("/login", req.url);
  loginUrl.searchParams.set("from", pathname);
  return NextResponse.redirect(loginUrl);
}

export const config = {
  // Exclure tous les assets statiques et les routes API propres à Next
  matcher: [
    "/((?!_next/static|_next/image|favicon.ico|fonts/|textures/|api/auth|$).*)",
  ],
};
