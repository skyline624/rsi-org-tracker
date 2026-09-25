/**
 * Contexte d'appel à l'API pour les pages serveur (RSC).
 *
 * Les pages chargent leurs données avec le JWT de l'utilisateur : c'est l'API
 * qui valide la signature. Un cookie forgé n'obtient donc qu'un 401, converti
 * ici en redirection vers /login.
 */

import { redirect } from "next/navigation";
import { ApiError } from "@/lib/api/errors";
import { getSession, sessionCtx } from "./session";

export interface AuthCtx {
  bearerToken: string;
  clientIp?: string;
}

function isUnauthorized(err: unknown): boolean {
  return err instanceof ApiError && err.status === 401;
}

/** Contexte authentifié, ou redirection vers /login s'il n'y a pas de session. */
export async function requireAuthCtx(): Promise<AuthCtx> {
  const session = await getSession();
  if (!session) redirect("/login");
  return sessionCtx(session);
}

/** Attend `promise` ; un 401 de l'API devient une redirection vers /login. */
export async function withAuthRedirect<T>(promise: Promise<T>): Promise<T> {
  try {
    return await promise;
  } catch (err) {
    if (isUnauthorized(err)) redirect("/login");
    throw err;
  }
}

/** Pour les pages en `Promise.allSettled` : redirige si un appel a reçu un 401. */
export function redirectIfUnauthorized(
  results: PromiseSettledResult<unknown>[],
): void {
  if (results.some((r) => r.status === "rejected" && isUnauthorized(r.reason))) {
    redirect("/login");
  }
}
