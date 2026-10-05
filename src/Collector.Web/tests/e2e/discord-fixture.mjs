// Loopback-only API fixture for browser tests. It issues disposable signed sessions
// and records mutations; no real Discord, citizen data or credentials are involved.
import { createServer } from "node:http";
import { exportJWK, generateKeyPair, SignJWT, jwtVerify } from "jose";

const GUILD = "123456789012345678";
const ROLE = "234567890123456789";
const USER = "345678901234567890";
const now = "2026-09-30T12:00:00Z";
const { publicKey, privateKey } = await generateKeyPair("RS256");
const jwk = { ...(await exportJWK(publicKey)), kid: "discord-e2e", alg: "RS256", use: "sig" };
let state;
function reset(input = {}) {
  state = {
    orgSid: input.orgSid === null ? null : "TEST", guildName: input.guildName ?? null,
    isRank: true, rankOrder: 10, rsiRankLabel: "Pilote", suggestion: input.suggestion ?? true,
    suggestionConfidence: input.suggestionConfidence ?? "medium",
    detectedOrg: input.detectedOrg ?? null,
    keys: [], mutations: [], searches: [],
  };
}
reset();

const hostileName = "Corsaires <img src=x onerror=window.__discordXss=1>\u202E";
const orgs = [
  { sid: "TEST", name: "Corpo de test" },
  { sid: "NEW", name: "Nouvelle Organisation Interstellaire" },
  { sid: "LIBERASTRA", name: "Libérastra" },
  { sid: "LIBER2", name: "LIBERASTRA Exploration" },
  { sid: "X", name: "Corpo X" },
  { sid: "AURELIS", name: "Aurelis Ops" },
];
const rank = () => ({ roleId: ROLE, name: "Pilote", color: "#00aaff" });
const summary = () => ({
  guildId: GUILD, name: state.guildName ?? hostileName, iconHash: null, orgSid: state.orgSid,
  orgName: orgs.find(o => o.sid === state.orgSid)?.name ?? null, orgMappedBy: state.orgSid ? "admin" : null,
  activeMembers: 1, rankDistribution: [{ ...rank(), count: 1 }],
  lastSync: { receivedAt: now, isComplete: false, method: "cache", submittedBy: "admin", massDepartureDetected: true },
  lastCompleteSyncAt: now,
  detectedOrg: state.orgSid ? null : state.detectedOrg,
});
const role = () => ({ ...rank(), position: 10, hoist: true, managed: false,
  isRank: state.isRank, rankOrder: state.rankOrder, rsiRankLabel: state.rsiRankLabel, deleted: false, memberCount: 1 });
const member = { discordUserId: USER, username: "pilote", globalName: "Pilote", nick: "Pilote42",
  isBot: false, joinedAt: now, firstSeenAt: now, lastSeenAt: now, leftAt: null,
  rank: rank(), roles: [rank()], links: [{ handle: "Pilote42", citizenId: 42, displayName: "Pilote 42" }],
  rsiRank: "Pilote", reconciliation: "ok", multipleLinks: false };
const page = items => ({ items, total: items.length, page: 1, pageSize: 50, totalPages: 1 });
const problem = (res, status, detail) => reply(res, status, { status, title: status === 403 ? "Forbidden" : "Not Found", detail });
function reply(res, status, body) {
  res.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store" });
  res.end(body === undefined ? undefined : JSON.stringify(body));
}

createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://127.0.0.1:3335");
    const path = url.pathname;
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const text = Buffer.concat(chunks).toString();
    const body = text ? JSON.parse(text) : undefined;
    if (path === "/health") return reply(res, 200, {});
    if (path === "/__fixture/reset" && req.method === "POST") { reset(body ?? {}); return reply(res, 204); }
    if (path === "/__fixture/state") return reply(res, 200, state);
    if (path === "/api/auth/jwks") return reply(res, 200, { keys: [jwk] });
    if (path === "/api/auth/login") {
      const admin = body?.username === "admin";
      const accessToken = await new SignJWT({ name: body?.username ?? "reader", role: admin ? "Admin" : "User" })
        .setProtectedHeader({ alg: "RS256", kid: jwk.kid }).setSubject(admin ? "1" : "2")
        .setIssuer("sc-tracker-api").setAudience("sc-tracker-clients").setIssuedAt().setExpirationTime("1h").sign(privateKey);
      return reply(res, 200, { accessToken, refreshToken: "fixture-refresh", expiresAt: new Date(Date.now() + 3600000).toISOString(),
        user: { id: admin ? 1 : 2, username: body?.username, email: "fixture@example.test", isAdmin: admin, createdAt: now } });
    }
    let actor;
    try { actor = (await jwtVerify((req.headers.authorization ?? "").replace(/^Bearer /, ""), publicKey)).payload; }
    catch { return reply(res, 401, { status: 401, title: "Unauthorized" }); }
    if (req.method !== "GET") state.mutations.push({ path, method: req.method, body: body ?? null });
    if (path === "/api/organizations/suggestions") {
      const raw = url.searchParams.get("query") ?? "";
      state.searches.push(raw.toLowerCase());
      const compact = text => text.normalize("NFKC").replace(/[^\p{L}\p{N}]/gu, "").toLowerCase();
      const query = compact(raw);
      const options = query ? orgs.filter(o => compact(o.sid).includes(query) || compact(o.name).includes(query)) : [];
      options.sort((a, b) => Number(b.sid.toLowerCase() === raw.trim().toLowerCase()) - Number(a.sid.toLowerCase() === raw.trim().toLowerCase())
        || Number(compact(b.name) === query) - Number(compact(a.name) === query) || a.sid.localeCompare(b.sid));
      return reply(res, 200, options.slice(0, 10));
    }
    if (path === "/api/organizations") {
      const query = (url.searchParams.get("search") ?? "").toLowerCase();
      state.searches.push(query);
      return reply(res, 200, page(orgs.filter(o => o.sid.toLowerCase().includes(query) || o.name.toLowerCase().includes(query))));
    }
    if (path.startsWith("/api/organizations/")) {
      const org = orgs.find(o => o.sid === path.split("/").at(-1));
      return org ? reply(res, 200, org) : problem(res, 404, "Corpo inconnue.");
    }
    if (path === "/api/discord/ingest-config") return reply(res, 200, { publicUrl: "https://tracker.example.test", certificateSha256: "AB:".repeat(31) + "AB" });
    if (path === "/api/admin/discord-token") return reply(res, 200, { configured: false });
    if (path === "/api/api-keys" && req.method === "GET") return reply(res, 200, state.keys);
    if (path === "/api/api-keys" && req.method === "POST") {
      const key = { id: state.keys.length + 1, name: body.name, keyPrefix: "sc_fixture", createdAt: now,
        lastUsedAt: null, expiresAt: body.expiresAt, scope: body.scope, isRevoked: false };
      state.keys.unshift(key);
      return reply(res, 201, { ...key, rawKey: "sc_fixture_DISPOSABLE_KEY" });
    }
    if (path.startsWith("/api/api-keys/") && req.method === "DELETE") {
      state.keys.find(k => k.id === Number(path.split("/").at(-1))).isRevoked = true;
      return reply(res, 204);
    }
    if (path === "/api/discord/guilds") return reply(res, 200, [summary()]);
    if (path === `/api/discord/guilds/${GUILD}`) return reply(res, 200, { ...summary(), roles: [role()], rsiRanks: state.orgSid ? ["Pilote", "Commandant"] : [], canEdit: actor.role === "Admin" || state.orgSid === null });
    if (path === `/api/discord/guilds/${GUILD}/members`) return reply(res, 200, page([member]));
    if (path === `/api/discord/guilds/${GUILD}/events`) return reply(res, 200, [{ id: 1, guildId: GUILD, discordUserId: USER, username: "pilote", type: "roles_changed",
      oldValue: "[]", newValue: JSON.stringify([{ id: ROLE, name: "Pilote" }]), occurredAt: null, notBefore: now, observedAt: now, submittedBy: "admin", rankChange: { from: null, to: "Pilote" } }]);
    if (path === `/api/discord/guilds/${GUILD}/syncs`) return reply(res, 200, [{ id: 1, receivedAt: now, collectedAt: now, submittedBy: "admin", method: "cache",
      declaredComplete: false, isComplete: false, isBaseline: false, massDepartureDetected: true, expectedCount: 2, collectedCount: 1,
      optedOutCount: 0, unknownRoleRefCount: 0, eventCount: 1, pluginVersion: "1.0.0" }]);
    if (path === `/api/discord/guilds/${GUILD}/discrepancies`) return reply(res, 200, { orgSid: state.orgSid, rsiOnlyAvailable: false,
      items: [{ kind: "rank_mismatch", handle: "Pilote42", citizenId: 42, discordUserId: USER, discordName: "Pilote42", discordRank: "Pilote", rsiRank: "Commandant" }],
      totals: { discordActive: 1, discordLinked: 1, rsiVisible: 1, rsiRedacted: 0, rsiHidden: 0, rsiTotalRows: 1, rsiCountsAt: now, rsiBreakdownKnown: true } });
    if (path === `/api/discord/guilds/${GUILD}/suggestions`) return reply(res, 200, state.suggestion ? [{ discordUserId: USER, discordName: "Pilote42", matchedToken: "Pilote42", handle: "Pilote42", citizenId: 42, displayName: "Pilote 42", confidence: state.suggestionConfidence,
      strongVia: state.suggestionConfidence === "strong" ? "tag" : null, strongOrgSid: state.suggestionConfidence === "strong" ? "AURELIS" : null }] : []);
    if (path === "/api/discord/link-rejections" && req.method === "POST") { state.suggestion = false; return reply(res, 201, { id: 1 }); }
    if (path === "/api/discord/link-rejections/1" && req.method === "DELETE") { state.suggestion = true; return reply(res, 204); }
    if (path === "/api/discord/links" && req.method === "POST") { state.suggestion = false; return reply(res, 201, { entityId: 1, handle: "Pilote42" }); }
    if (path === `/api/discord/guilds/${GUILD}/org` && req.method === "PUT") {
      if (actor.role !== "Admin" && state.orgSid !== null) return problem(res, 403, "Réservé au responsable du serveur.");
      if (body.orgSid !== null && !orgs.some(o => o.sid === body.orgSid)) return problem(res, 400, "Corpo inconnue.");
      state.orgSid = body.orgSid; return reply(res, 200, summary());
    }
    if (path === `/api/discord/guilds/${GUILD}/roles/${ROLE}` && req.method === "PUT") {
      if (actor.role !== "Admin") return problem(res, 403, "Réservé au responsable du serveur.");
      Object.assign(state, body); return reply(res, 200, role());
    }
    if (path === "/api/discord/multi") return reply(res, 200, page([{ discordUserId: USER, username: "pilote", globalName: "Pilote",
      guilds: [{ guildId: GUILD, guildName: hostileName, orgSid: state.orgSid, rank: "Pilote" }, { guildId: "456789012345678901", guildName: "Recrutement", orgSid: "TEST", rank: null }], links: member.links, rsiOrgs: [{ sid: "TEST", rank: "Pilote" }] }]));
    return problem(res, 404, "Ressource inconnue.");
  } catch (error) {
    console.error(error);
    reply(res, 500, { status: 500, title: "Fixture error" });
  }
}).listen(3335, "127.0.0.1", () => console.log("Discord browser fixture ready on loopback:3335"));
