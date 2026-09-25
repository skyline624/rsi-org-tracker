"use server";

import { cookies } from "next/headers";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiPost } from "@/lib/api/client";
import type { AuthResponse } from "@/lib/api/types";
import {
  COOKIE_ACCESS,
  COOKIE_REFRESH,
  accessCookieOptions,
  refreshCookieOptions,
} from "@/lib/auth/cookies";

export interface ChangePasswordResult {
  ok: boolean;
  error?: string;
}

export async function changePasswordAction(
  currentPassword: string,
  newPassword: string,
): Promise<ChangePasswordResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (newPassword.length < 8)
    return { ok: false, error: "Le nouveau mot de passe doit faire au moins 8 caractères." };

  try {
    // The API revokes every session (this one included) and returns fresh tokens.
    const auth = await apiPost<AuthResponse>(
      "/api/auth/change-password",
      { currentPassword, newPassword },
      sessionCtx(session),
    );
    const jar = await cookies();
    jar.set(COOKIE_ACCESS, auth.accessToken, accessCookieOptions(new Date(auth.expiresAt)));
    jar.set(COOKIE_REFRESH, auth.refreshToken, refreshCookieOptions());
    return { ok: true };
  } catch (e) {
    const msg = e instanceof Error ? e.message : "";
    // L'API renvoie 401 quand le mot de passe actuel est faux.
    if (/401|unauthor/i.test(msg))
      return { ok: false, error: "Mot de passe actuel incorrect." };
    return { ok: false, error: msg || "Échec du changement de mot de passe." };
  }
}
