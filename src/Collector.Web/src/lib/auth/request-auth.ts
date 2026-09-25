/**
 * Contrôle d'accès exécuté par le middleware à chaque requête de page.
 *
 * Le site est privé : hors pages publiques, il faut un access token dont la
 * signature (RS256) est vérifiée. S'il manque, a expiré ou expire dans la minute,
 * on le renouvelle avec le refresh token ; les nouveaux cookies sont posés sur la
 * réponse ET réinjectés dans la requête, pour que la page serveur voie déjà le
 * nouveau token. Sinon : redirection vers /login et suppression des cookies.
 */

import { NextRequest, NextResponse } from "next/server";
import type { JWTPayload } from "jose";
import {
  COOKIE_ACCESS,
  COOKIE_REFRESH,
  accessCookieOptions,
  refreshCookieOptions,
} from "./cookies";
import { firstForwardedIp } from "@/lib/api/client-ip";

export interface RefreshedTokens {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
}

export interface AuthDeps {
  verify: (token: string) => Promise<JWTPayload | null>;
  refresh: (refreshToken: string, clientIp?: string) => Promise<RefreshedTokens | null>;
}

// Seules ces routes sont accessibles sans compte.
const PUBLIC_PREFIXES = ["/login"];

/** En dessous, l'access token est renouvelé avant d'être utilisé. */
const REFRESH_MARGIN_SECONDS = 60;
/** Durée pendant laquelle un refresh en cours est partagé entre requêtes concurrentes. */
const REFRESH_DEDUPE_MS = 10_000;

const inFlight = new Map<string, Promise<RefreshedTokens | null>>();

/**
 * Plusieurs requêtes d'une même page arrivent en parallèle avec le même refresh
 * token : un seul appel à l'API, sinon la rotation invaliderait les suivants.
 */
function refreshOnce(refreshToken: string, clientIp: string | undefined, deps: AuthDeps) {
  let pending = inFlight.get(refreshToken);
  if (!pending) {
    pending = deps.refresh(refreshToken, clientIp).catch(() => null);
    inFlight.set(refreshToken, pending);
    setTimeout(() => inFlight.delete(refreshToken), REFRESH_DEDUPE_MS).unref?.();
  }
  return pending;
}

function isPublic(pathname: string) {
  return PUBLIC_PREFIXES.some((p) => pathname === p || pathname.startsWith(`${p}/`));
}

/**
 * URL publique de /login. Derrière nginx, req.url porte l'hôte interne ; nginx
 * transmet l'hôte public dans `Host` (X-Forwarded-Host, lui, vient du client et
 * n'est pas fiable).
 */
function loginRedirect(req: NextRequest) {
  const host = req.headers.get("host") ?? req.nextUrl.host;
  const proto = req.headers.get("x-forwarded-proto") ?? "https";
  const url = new URL(`${proto}://${host}/login`);
  url.searchParams.set("from", req.nextUrl.pathname);
  const res = NextResponse.redirect(url);
  res.cookies.delete(COOKIE_ACCESS);
  res.cookies.delete(COOKIE_REFRESH);
  return res;
}

/** Extra headers handed to the page with the request (e.g. the CSP carrying the nonce). */
export type ForwardedHeaders = Record<string, string>;

function requestHeaders(req: NextRequest, extra: ForwardedHeaders) {
  const headers = new Headers(req.headers);
  for (const [name, value] of Object.entries(extra)) headers.set(name, value);
  return headers;
}

function pass(req: NextRequest, extra: ForwardedHeaders) {
  return Object.keys(extra).length === 0
    ? NextResponse.next()
    : NextResponse.next({ request: { headers: requestHeaders(req, extra) } });
}

function withTokens(req: NextRequest, tokens: RefreshedTokens, extra: ForwardedHeaders) {
  const cookies = req.cookies;
  cookies.set(COOKIE_ACCESS, tokens.accessToken);
  cookies.set(COOKIE_REFRESH, tokens.refreshToken);
  const headers = requestHeaders(req, extra);
  headers.set("cookie", cookies.toString());

  const res = NextResponse.next({ request: { headers } });
  res.cookies.set(
    COOKIE_ACCESS,
    tokens.accessToken,
    accessCookieOptions(new Date(tokens.expiresAt)),
  );
  res.cookies.set(COOKIE_REFRESH, tokens.refreshToken, refreshCookieOptions());
  return res;
}

export async function authenticateRequest(
  req: NextRequest,
  deps: AuthDeps,
  extra: ForwardedHeaders = {},
): Promise<NextResponse> {
  if (isPublic(req.nextUrl.pathname)) return pass(req, extra);

  const access = req.cookies.get(COOKIE_ACCESS)?.value;
  const refresh = req.cookies.get(COOKIE_REFRESH)?.value;

  const payload = access ? await deps.verify(access) : null;
  const secondsLeft = payload?.exp ? payload.exp - Math.floor(Date.now() / 1000) : 0;
  if (payload && secondsLeft > REFRESH_MARGIN_SECONDS) return pass(req, extra);

  if (refresh) {
    const clientIp = firstForwardedIp(req.headers.get("x-forwarded-for"));
    const tokens = await refreshOnce(refresh, clientIp, deps);
    if (tokens) return withTokens(req, tokens, extra);
  }

  // Refresh impossible : un token encore valide quelques secondes reste utilisable.
  if (payload && secondsLeft > 0) return pass(req, extra);
  return loginRedirect(req);
}
