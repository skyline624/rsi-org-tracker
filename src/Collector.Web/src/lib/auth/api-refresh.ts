/**
 * Appel à l'API pour renouveler une session, utilisé par le middleware.
 *
 * Distingue un refus (le refresh token n'est plus valable : la session est finie)
 * d'une indisponibilité (API qui redémarre, erreur réseau, 5xx, 429) : dans ce cas la
 * session doit survivre, sinon chaque déploiement déconnecterait tout le monde.
 */

import type { RefreshOutcome, RefreshedTokens } from "./request-auth";

const API_BASE = process.env.API_BASE_URL ?? "http://127.0.0.1:5000";

export async function refreshWithApi(
  refreshToken: string,
  clientIp?: string,
  fetchImpl: typeof fetch = fetch,
): Promise<RefreshOutcome> {
  let res: Response;
  try {
    res = await fetchImpl(`${API_BASE}/api/auth/refresh`, {
      method: "POST",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        ...(clientIp ? { "X-Forwarded-For": clientIp } : {}),
      },
      body: JSON.stringify({ refreshToken }),
      cache: "no-store",
    });
  } catch {
    return { status: "unavailable" };
  }

  if (res.status === 429 || res.status >= 500) return { status: "unavailable" };
  if (!res.ok) return { status: "rejected" };

  // A success without tokens is an API fault, not a verdict on the session.
  const body = (await res.json().catch(() => ({}))) as Partial<RefreshedTokens>;
  if (!body.accessToken || !body.refreshToken || !body.expiresAt) return { status: "unavailable" };
  return {
    status: "ok",
    tokens: {
      accessToken: body.accessToken,
      refreshToken: body.refreshToken,
      expiresAt: body.expiresAt,
    },
  };
}
