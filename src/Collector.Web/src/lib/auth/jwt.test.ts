import { beforeAll, describe, expect, it } from "vitest";
import {
  SignJWT,
  UnsecuredJWT,
  createRemoteJWKSet,
  customFetch,
  exportJWK,
  generateKeyPair,
  type CryptoKey,
} from "jose";
import { JWT_AUDIENCE, JWT_ISSUER, keepingLastKeys, verifyAccessToken } from "./jwt";

let privateKey: CryptoKey;
let publicKey: CryptoKey;

beforeAll(async () => {
  ({ privateKey, publicKey } = await generateKeyPair("RS256"));
});

function token(
  opts: { issuer?: string; audience?: string; expiresIn?: string } = {},
) {
  return new SignJWT({ unique_name: "pilot" })
    .setProtectedHeader({ alg: "RS256" })
    .setSubject("42")
    .setIssuer(opts.issuer ?? JWT_ISSUER)
    .setAudience(opts.audience ?? JWT_AUDIENCE)
    .setIssuedAt()
    .setExpirationTime(opts.expiresIn ?? "15m");
}

describe("verifyAccessToken", () => {
  it("accepts an RS256 token signed by the API key", async () => {
    const jwt = await token().sign(privateKey);

    const payload = await verifyAccessToken(jwt, publicKey);

    expect(payload?.sub).toBe("42");
  });

  it("rejects an expired token", async () => {
    const jwt = await token({ expiresIn: "-5m" }).sign(privateKey);

    expect(await verifyAccessToken(jwt, publicKey)).toBeNull();
  });

  it.each([
    ["issuer", { issuer: "someone-else" }],
    ["audience", { audience: "someone-else" }],
  ])("rejects a token with the wrong %s", async (_, opts) => {
    const jwt = await token(opts).sign(privateKey);

    expect(await verifyAccessToken(jwt, publicKey)).toBeNull();
  });

  it("rejects a token signed with another RSA key", async () => {
    const other = await generateKeyPair("RS256");
    const jwt = await token().sign(other.privateKey);

    expect(await verifyAccessToken(jwt, publicKey)).toBeNull();
  });

  it("rejects an HS256 token (legacy shared secret)", async () => {
    const jwt = await new SignJWT({})
      .setProtectedHeader({ alg: "HS256" })
      .setSubject("1")
      .setIssuer(JWT_ISSUER)
      .setAudience(JWT_AUDIENCE)
      .setExpirationTime("15m")
      .sign(new TextEncoder().encode("legacy-secret-0123456789abcdef0123"));

    expect(await verifyAccessToken(jwt, publicKey)).toBeNull();
  });

  it("rejects an unsigned token", async () => {
    const jwt = new UnsecuredJWT({ sub: "1" })
      .setIssuer(JWT_ISSUER)
      .setAudience(JWT_AUDIENCE)
      .setExpirationTime("15m")
      .encode();

    expect(await verifyAccessToken(jwt, publicKey)).toBeNull();
  });

  it("rejects garbage", async () => {
    expect(await verifyAccessToken("x", publicKey)).toBeNull();
  });
});

describe("keepingLastKeys", () => {
  it("keeps verifying with the last keys served while the API cannot be reached", async () => {
    const jwk = { ...(await exportJWK(publicKey)), alg: "RS256", kid: "k1" };
    let apiUp = true;
    const remote = createRemoteJWKSet(new URL("http://api.invalid/api/auth/jwks"), {
      cacheMaxAge: 0, // every verification wants fresh keys
      [customFetch]: async () => {
        if (!apiUp) throw new TypeError("fetch failed");
        return new Response(JSON.stringify({ keys: [jwk] }));
      },
    });
    const keys = keepingLastKeys(remote);
    const jwt = await token().sign(privateKey);
    expect(await verifyAccessToken(jwt, keys)).not.toBeNull();

    apiUp = false;

    expect((await verifyAccessToken(jwt, keys))?.sub).toBe("42");
    const other = await generateKeyPair("RS256");
    expect(await verifyAccessToken(await token().sign(other.privateKey), keys)).toBeNull();
  });
});
