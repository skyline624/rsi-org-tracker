"use server";

import { z } from "zod";
import { revalidatePath } from "next/cache";
import { apiDelete, apiGet, apiPost, apiPut } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import type {
  DiscordGuildSummaryDto,
  DiscordLinkCreatedDto,
  DiscordLinkRejectionCreatedDto,
  DiscordRoleDto,
} from "@/lib/api/types";
import { getSession, sessionCtx } from "@/lib/auth/session";
import {
  INVALID_ARGUMENTS,
  citizenIdSchema,
  handleSchema,
  idSchema,
  rankOrderSchema,
  rsiRankLabelSchema,
  searchQuerySchema,
  sidSchema,
  snowflakeSchema,
} from "@/lib/validation";

/**
 * Discord roster configuration and link mutations (spec § 11). Any client can call a server action
 * with any values: every argument is validated first, so a malformed call never reaches
 * the session or the API, and every outcome is returned, never thrown.
 */
export interface DiscordActionResult<T = undefined> {
  ok: boolean;
  data?: T;
  error?: string;
}

type ApiCtx = ReturnType<typeof sessionCtx>;

const NOT_SIGNED_IN = "Non authentifié.";

const orgSidSchema = sidSchema.nullable();
const optionalCitizenIdSchema = citizenIdSchema.nullable();
const flagSchema = z.boolean();

const guildPath = (guildId: string) => `/api/discord/guilds/${encodeURIComponent(guildId)}`;

function invalid<T>(): DiscordActionResult<T> {
  return { ok: false, error: INVALID_ARGUMENTS };
}

/** The API's own explanation (the French ProblemDetails detail) rather than its status title. */
function describeError(e: unknown, fallback: string): string {
  if (e instanceof ApiError) {
    const detail = e.problem.detail;
    return typeof detail === "string" && detail.trim() !== "" ? detail : e.message;
  }
  return fallback;
}

/** Calls the API as the signed-in user and reports any failure as text. */
async function asUser<T>(
  call: (ctx: ApiCtx) => Promise<T>,
  { fallback, relatedPaths = [] }: { fallback: string; relatedPaths?: string[] },
): Promise<DiscordActionResult<T>> {
  try {
    const session = await getSession();
    if (!session) return { ok: false, error: NOT_SIGNED_IN };
    const data = await call(sessionCtx(session));
    for (const path of relatedPaths) revalidatePath(path, "layout");
    return data === undefined ? { ok: true } : { ok: true, data };
  } catch (e) {
    return { ok: false, error: describeError(e, fallback) };
  }
}

export interface GuildOrgOption {
  sid: string;
  name: string;
}

/** An exact SID must remain selectable even when ten other names match the search. */
export async function searchGuildOrgsAction(query: unknown): Promise<GuildOrgOption[]> {
  const parsed = searchQuerySchema.safeParse(query);
  if (!parsed.success || !parsed.data.trim()) return [];
  const session = await getSession();
  if (!session) throw new Error(NOT_SIGNED_IN);
  const text = parsed.data.trim();
  const ctx = sessionCtx(session);
  const sid = sidSchema.safeParse(text);
  const [exact, found] = await Promise.allSettled([
    sid.success
      ? apiGet<GuildOrgOption>(`/api/organizations/${encodeURIComponent(sid.data.toUpperCase())}`, undefined, ctx)
      : Promise.resolve(null),
    apiGet<{ items: GuildOrgOption[] }>("/api/organizations", { search: text, pageSize: 10 }, ctx),
  ]);
  const first = exact.status === "fulfilled" && exact.value ? [exact.value] : [];
  if (found.status === "rejected" && first.length === 0)
    throw new Error("Recherche de corpos indisponible. Réessaie.");
  const options = [...first, ...(found.status === "fulfilled" ? found.value.items ?? [] : [])];
  return options.filter((org, index) => options.findIndex(other => other.sid === org.sid) === index)
    .map(({ sid, name }) => ({ sid, name }));
}

/** The target of a suggestion: the Discord account, the citizen number when known, the RSI handle. */
function parseLinkTarget(discordUserId: unknown, citizenId: unknown, handle: unknown) {
  const user = snowflakeSchema.safeParse(discordUserId);
  const citizen = optionalCitizenIdSchema.safeParse(citizenId);
  const rsiHandle = handleSchema.safeParse(handle);
  if (!user.success || !citizen.success || !rsiHandle.success) return null;
  return { discordUserId: user.data, citizenId: citizen.data, handle: rsiHandle.data };
}

/** Maps a server to an org by SID (the API upper-cases it and refuses an unknown one); null unmaps it. */
export async function mapGuildOrgAction(
  guildId: unknown,
  orgSid: unknown,
): Promise<DiscordActionResult<DiscordGuildSummaryDto>> {
  const guild = snowflakeSchema.safeParse(guildId);
  const sid = orgSidSchema.safeParse(orgSid);
  if (!guild.success || !sid.success) return invalid();
  const path = `${guildPath(guild.data)}/org`;
  const body = { orgSid: sid.data };
  return asUser((ctx) => apiPut<DiscordGuildSummaryDto>(path, body, ctx), {
    fallback: "Échec du rattachement du serveur.",
    relatedPaths: ["/discord", "/orgs"],
  });
}

/** Makes a role a rank or not, with its order and the RSI rank it stands for. */
export async function updateGuildRoleAction(
  guildId: unknown,
  roleId: unknown,
  isRank: unknown,
  rankOrder: unknown,
  rsiRankLabel: unknown,
): Promise<DiscordActionResult<DiscordRoleDto>> {
  const guild = snowflakeSchema.safeParse(guildId);
  const role = snowflakeSchema.safeParse(roleId);
  const rank = flagSchema.safeParse(isRank);
  const order = rankOrderSchema.safeParse(rankOrder);
  const label = rsiRankLabelSchema.safeParse(rsiRankLabel);
  if (!guild.success || !role.success || !rank.success || !order.success || !label.success) return invalid();
  const path = `${guildPath(guild.data)}/roles/${encodeURIComponent(role.data)}`;
  const body = { isRank: rank.data, rankOrder: order.data, rsiRankLabel: label.data ? label.data : null };
  return asUser((ctx) => apiPut<DiscordRoleDto>(path, body, ctx), {
    fallback: "Échec de l'enregistrement du rôle.",
    relatedPaths: ["/discord"],
  });
}

/** Validates a suggestion: links the Discord account to the citizen. */
export async function acceptSuggestionAction(
  discordUserId: unknown,
  citizenId: unknown,
  handle: unknown,
): Promise<DiscordActionResult<DiscordLinkCreatedDto>> {
  const body = parseLinkTarget(discordUserId, citizenId, handle);
  if (!body) return invalid();
  return asUser((ctx) => apiPost<DiscordLinkCreatedDto>("/api/discord/links", body, ctx), {
    fallback: "Échec de la validation du lien.",
    relatedPaths: ["/discord", "/users", "/orgs"],
  });
}

/** Ignores a suggestion: it is no longer proposed; the returned id undoes it. */
export async function rejectSuggestionAction(
  discordUserId: unknown,
  citizenId: unknown,
  handle: unknown,
): Promise<DiscordActionResult<DiscordLinkRejectionCreatedDto>> {
  const body = parseLinkTarget(discordUserId, citizenId, handle);
  if (!body) return invalid();
  return asUser((ctx) => apiPost<DiscordLinkRejectionCreatedDto>("/api/discord/link-rejections", body, ctx), {
    fallback: "Échec du rejet de la suggestion.",
    relatedPaths: ["/discord"],
  });
}

/** Cancels a rejection (its author or an admin): the suggestion comes back. */
export async function undoRejectionAction(id: unknown): Promise<DiscordActionResult> {
  const parsed = idSchema.safeParse(id);
  if (!parsed.success) return invalid();
  const path = `/api/discord/link-rejections/${parsed.data}`;
  return asUser((ctx) => apiDelete<undefined>(path, ctx), {
    fallback: "Échec de l'annulation du rejet.", relatedPaths: ["/discord"],
  });
}
