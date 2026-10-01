"use server";

import {
  INVALID_ARGUMENTS,
  apiKeyNameSchema,
  idSchema,
  ingestKeyExpiryDaysSchema,
} from "@/lib/validation";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiDelete, apiPost } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import type { CreatedApiKeyDto } from "@/lib/api/types";

/** The only kind of key the site creates: it uploads Discord rosters and nothing else. */
const DISCORD_INGEST_SCOPE = "discord:ingest";
const DAY_MS = 24 * 60 * 60 * 1000;

function describeError(error: unknown, fallback: string): string {
  if (!(error instanceof ApiError)) return fallback;
  const detail = error.problem.detail;
  return typeof detail === "string" && detail.trim() !== "" ? detail : error.message;
}

export interface CreateDiscordIngestKeyResult {
  ok: boolean;
  /** Carries the raw key: the panel shows it once, the site never stores it. */
  data?: CreatedApiKeyDto;
  error?: string;
}

export interface RevokeApiKeyResult {
  ok: boolean;
  error?: string;
}

export async function createDiscordIngestKeyAction(
  name: unknown,
  expiresInDays: unknown,
): Promise<CreateDiscordIngestKeyResult> {
  const parsedName = apiKeyNameSchema.safeParse(name);
  const parsedDays = ingestKeyExpiryDaysSchema.safeParse(expiresInDays);
  if (!parsedName.success || !parsedDays.success) return { ok: false, error: INVALID_ARGUMENTS };
  try {
    const session = await getSession();
    if (!session) return { ok: false, error: "Non authentifié." };
    // Computed on the server, whose clock is the API's: it refuses more than 365 days.
    const expiresAt = new Date(Date.now() + parsedDays.data * DAY_MS).toISOString();
    const data = await apiPost<CreatedApiKeyDto>(
      "/api/api-keys",
      { name: parsedName.data, expiresAt, scope: DISCORD_INGEST_SCOPE },
      sessionCtx(session),
    );
    return { ok: true, data };
  } catch (e) {
    return { ok: false, error: describeError(e, "Échec de la création de la clé.") };
  }
}

export async function revokeApiKeyAction(id: unknown): Promise<RevokeApiKeyResult> {
  const parsed = idSchema.safeParse(id);
  if (!parsed.success) return { ok: false, error: INVALID_ARGUMENTS };
  try {
    const session = await getSession();
    if (!session) return { ok: false, error: "Non authentifié." };
    await apiDelete(`/api/api-keys/${parsed.data}`, sessionCtx(session));
    return { ok: true };
  } catch (e) {
    return { ok: false, error: describeError(e, "Échec de la révocation.") };
  }
}
