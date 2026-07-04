"use server";

import { getSession } from "@/lib/auth/session";
import { apiPut } from "@/lib/api/client";

export interface DiscordTokenResult {
  ok: boolean;
  error?: string;
}

export async function setDiscordTokenAction(token: string): Promise<DiscordTokenResult> {
  const session = await getSession();
  if (!session?.isAdmin) return { ok: false, error: "Réservé aux administrateurs." };
  const t = token.trim();
  if (t.length < 20) return { ok: false, error: "Token invalide (trop court)." };
  try {
    await apiPut("/api/admin/discord-token", { token: t }, { bearerToken: session.accessToken });
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec de l'enregistrement." };
  }
}
