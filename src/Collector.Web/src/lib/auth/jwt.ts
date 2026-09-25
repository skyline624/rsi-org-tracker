/**
 * Vérification des access tokens émis par l'API.
 *
 * L'API signe en RS256 et publie sa clé publique sur `/api/auth/jwks` : le front
 * vérifie la signature sans détenir de clé capable d'émettre des tokens.
 */

import {
  createLocalJWKSet,
  createRemoteJWKSet,
  jwtVerify,
  type JWTPayload,
  type JWTVerifyGetKey,
  type RemoteJWKSet,
} from "jose";

export const JWT_ISSUER = "sc-tracker-api";
export const JWT_AUDIENCE = "sc-tracker-clients";

type VerificationKey = Parameters<typeof jwtVerify>[1];

let remoteKeySet: JWTVerifyGetKey | undefined;

/** JWKS de l'API, mis en cache (et rechargé si un `kid` inconnu apparaît). */
function apiKeySet(): JWTVerifyGetKey {
  remoteKeySet ??= keepingLastKeys(
    createRemoteJWKSet(
      new URL("/api/auth/jwks", process.env.API_BASE_URL ?? "http://127.0.0.1:5000"),
      { cacheMaxAge: 10 * 60_000, cooldownDuration: 30_000 },
    ),
  );
  return remoteKeySet;
}

/**
 * Résout la clé avec le JWKS distant ; si l'API ne répond pas (redémarrage,
 * déploiement) alors que le cache doit être rechargé, les dernières clés reçues
 * restent valables : un token encore bon ne doit pas faire déconnecter l'utilisateur.
 */
export function keepingLastKeys(remote: RemoteJWKSet): JWTVerifyGetKey {
  return async (protectedHeader, token) => {
    try {
      return await remote(protectedHeader, token);
    } catch (err) {
      const last = remote.jwks();
      if (!last) throw err;
      return createLocalJWKSet(last)(protectedHeader, token);
    }
  };
}

/** Payload du token s'il est signé par l'API, non expiré et destiné à ce front ; sinon null. */
export async function verifyAccessToken(
  token: string,
  key: VerificationKey = apiKeySet(),
): Promise<JWTPayload | null> {
  try {
    const { payload } = await jwtVerify(token, key as JWTVerifyGetKey, {
      issuer: JWT_ISSUER,
      audience: JWT_AUDIENCE,
      algorithms: ["RS256"],
      clockTolerance: 30,
    });
    return payload;
  } catch {
    return null;
  }
}
