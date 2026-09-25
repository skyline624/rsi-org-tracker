/**
 * Helpers de session côté serveur (RSC / route handlers / Server Actions).
 *
 * La signature du JWT (RS256) est vérifiée avec la clé publique de l'API :
 * un cookie forgé ne donne jamais de session, et donc jamais `isAdmin`.
 */

import { cookies, headers } from "next/headers";
import { firstForwardedIp } from "@/lib/api/client-ip";
import { COOKIE_ACCESS } from "./cookies";
import { verifyAccessToken } from "./jwt";

export interface Session {
  userId: number;
  username: string;
  email?: string;
  isAdmin: boolean;
  accessToken: string;
  /** IP du navigateur (X-Forwarded-For de nginx), à relayer à l'API. */
  clientIp?: string;
  expiresAt: Date;
}

interface JwtPayload {
  sub?: string;
  nameid?: string;
  name?: string;
  unique_name?: string;
  email?: string;
  role?: string | string[];
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"?: string;
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"?: string;
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"?: string;
  "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"?: string | string[];
  exp?: number;
}

/**
 * Retourne la session courante ou `null` si non authentifié.
 * Lit le cookie `sct_access` (posé par le BFF ou renouvelé par le middleware)
 * et n'accepte que les tokens signés par l'API et non expirés.
 */
export async function getSession(): Promise<Session | null> {
  const jar = await cookies();
  const token = jar.get(COOKIE_ACCESS)?.value;
  if (!token) return null;

  const payload = (await verifyAccessToken(token)) as JwtPayload | null;
  if (!payload) return null;

  const nameId =
    payload[
      "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"
    ] ?? payload.nameid ?? payload.sub;
  const name =
    payload[
      "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"
    ] ?? payload.name ?? payload.unique_name;
  const email =
    payload[
      "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"
    ] ?? payload.email;
  const role =
    payload["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] ??
    payload.role;
  const isAdmin = Array.isArray(role)
    ? role.includes("Admin")
    : role === "Admin";

  if (!nameId || !name) return null;

  return {
    userId: Number(nameId),
    username: String(name),
    email: email ? String(email) : undefined,
    isAdmin,
    accessToken: token,
    clientIp: firstForwardedIp((await headers()).get("x-forwarded-for")),
    expiresAt: payload.exp ? new Date(payload.exp * 1000) : new Date(0),
  };
}

/** Contexte d'appel à l'API pour cette session (JWT + IP du client). */
export function sessionCtx(session: Session) {
  return { bearerToken: session.accessToken, clientIp: session.clientIp };
}
