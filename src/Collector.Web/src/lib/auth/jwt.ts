/**
 * Vérification des access tokens émis par l'API.
 *
 * L'API signe en RS256 et publie sa clé publique sur `/api/auth/jwks` : le front
 * vérifie la signature sans détenir de clé capable d'émettre des tokens.
 */

import {
  createRemoteJWKSet,
  jwtVerify,
  type JWTPayload,
  type JWTVerifyGetKey,
} from "jose";

export const JWT_ISSUER = "sc-tracker-api";
export const JWT_AUDIENCE = "sc-tracker-clients";

type VerificationKey = Parameters<typeof jwtVerify>[1];

let remoteKeySet: JWTVerifyGetKey | undefined;

/** JWKS de l'API, mis en cache (et rechargé si un `kid` inconnu apparaît). */
function apiKeySet(): JWTVerifyGetKey {
  remoteKeySet ??= createRemoteJWKSet(
    new URL("/api/auth/jwks", process.env.API_BASE_URL ?? "https://localhost:5001"),
    { cacheMaxAge: 10 * 60_000, cooldownDuration: 30_000 },
  );
  return remoteKeySet;
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
