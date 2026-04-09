/**
 * Helpers de session côté serveur (RSC / route handlers).
 *
 * v1 : on décode le JWT sans vérifier sa signature — la signature n'est
 * connue que du serveur .NET, et notre rôle ici est juste de savoir si
 * l'utilisateur est connecté pour le routage UI. Les opérations sensibles
 * passent de toute façon par l'API qui re-valide le token.
 */

import { cookies } from "next/headers";
import { COOKIE_ACCESS } from "./cookies";

export interface Session {
  userId: number;
  username: string;
  email?: string;
  isAdmin: boolean;
  accessToken: string;
  expiresAt: Date;
}

interface JwtPayload {
  sub?: string;
  nameid?: string;
  name?: string;
  email?: string;
  role?: string | string[];
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"?: string;
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"?: string;
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"?: string;
  "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"?: string | string[];
  exp?: number;
}

function decodeJwt(token: string): JwtPayload | null {
  try {
    const parts = token.split(".");
    if (parts.length < 2) return null;
    const payloadPart = parts[1];
    if (!payloadPart) return null;
    const b64 = payloadPart.replace(/-/g, "+").replace(/_/g, "/");
    const padded = b64 + "=".repeat((4 - (b64.length % 4)) % 4);
    const json = Buffer.from(padded, "base64").toString("utf-8");
    return JSON.parse(json) as JwtPayload;
  } catch {
    return null;
  }
}

/**
 * Retourne la session courante ou `null` si non authentifié.
 * Lit le cookie `sct_access` (posé par le BFF).
 */
export async function getSession(): Promise<Session | null> {
  const jar = await cookies();
  const token = jar.get(COOKIE_ACCESS)?.value;
  if (!token) return null;

  const payload = decodeJwt(token);
  if (!payload) return null;
  if (payload.exp && payload.exp * 1000 < Date.now()) return null;

  const nameId =
    payload[
      "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"
    ] ?? payload.nameid ?? payload.sub;
  const name =
    payload[
      "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"
    ] ?? payload.name;
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
    expiresAt: payload.exp ? new Date(payload.exp * 1000) : new Date(0),
  };
}

/** Raccourci : force une session. À utiliser dans les layouts authentifiés. */
export async function requireSession(): Promise<Session> {
  const s = await getSession();
  if (!s) throw new Error("Unauthorized");
  return s;
}
