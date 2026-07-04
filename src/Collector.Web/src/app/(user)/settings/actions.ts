"use server";

import { getSession } from "@/lib/auth/session";
import { apiPost } from "@/lib/api/client";

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
    await apiPost(
      "/api/auth/change-password",
      { currentPassword, newPassword },
      { bearerToken: session.accessToken },
    );
    return { ok: true };
  } catch (e) {
    const msg = e instanceof Error ? e.message : "";
    // L'API renvoie 401 quand le mot de passe actuel est faux.
    if (/401|unauthor/i.test(msg))
      return { ok: false, error: "Mot de passe actuel incorrect." };
    return { ok: false, error: msg || "Échec du changement de mot de passe." };
  }
}
