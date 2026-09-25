"use server";

import { INVALID_ARGUMENTS, handleSchema, idSchema, linkProviderSchema, valid } from "@/lib/validation";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiGet, apiPost, apiDelete } from "@/lib/api/client";

export interface LinkDto {
  id: number;
  provider: string;
  value: string;
  authorUsername: string;
  createdAt: string;
}

export interface LinkResult {
  ok: boolean;
  link?: LinkDto;
  error?: string;
}

export async function createLinkAction(
  handle: string,
  provider: string,
  value: string,
): Promise<LinkResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!valid(handleSchema, handle) || !valid(linkProviderSchema, provider) || typeof value !== "string" || value.length > 200) return { ok: false, error: INVALID_ARGUMENTS };
  const v = value.trim();
  if (!v) return { ok: false, error: "Valeur vide." };
  try {
    const link = await apiPost<LinkDto>(
      `/api/users/${encodeURIComponent(handle)}/links`,
      { provider, value: v },
      sessionCtx(session),
    );
    return { ok: true, link };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteLinkAction(id: number): Promise<LinkResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!valid(idSchema, id)) return { ok: false, error: INVALID_ARGUMENTS };
  try {
    await apiDelete(`/api/links/${id}`, sessionCtx(session));
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

/**
 * Persists the Twitch channel detected on UEX as a link, if not already present.
 * Idempotent; returns whether a new link was actually created (to trigger a refresh).
 */
export async function autoAddTwitchLinkAction(
  handle: string,
  username: string,
): Promise<{ added: boolean }> {
  const session = await getSession();
  if (!session) return { added: false };
  if (!valid(handleSchema, handle) || typeof username !== "string" || username.length > 200) return { added: false };
  const value = username.trim();
  if (!value) return { added: false };
  const opts = sessionCtx(session);
  try {
    const links = await apiGet<LinkDto[]>(
      `/api/users/${encodeURIComponent(handle)}/links`,
      undefined,
      opts,
    );
    if (
      links.some((l) => l.provider === "twitch" && l.value.toLowerCase() === value.toLowerCase())
    )
      return { added: false };
    await apiPost(`/api/users/${encodeURIComponent(handle)}/links`, { provider: "twitch", value }, opts);
    return { added: true };
  } catch {
    return { added: false };
  }
}
