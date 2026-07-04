"use server";

import { getSession } from "@/lib/auth/session";
import { apiPost, apiPut, apiDelete } from "@/lib/api/client";

export interface AdminUserDto {
  id: number;
  username: string;
  email: string;
  isAdmin: boolean;
  isBanned: boolean;
  createdAt: string;
  lastLoginAt: string | null;
  apiKeyCount: number;
}

export interface AccountResult {
  ok: boolean;
  user?: AdminUserDto;
  error?: string;
}

async function requireAdmin() {
  const session = await getSession();
  return session?.isAdmin ? session : null;
}

export async function createAccountAction(input: {
  username: string;
  email: string;
  password: string;
  isAdmin: boolean;
}): Promise<AccountResult> {
  const session = await requireAdmin();
  if (!session) return { ok: false, error: "Réservé aux administrateurs." };
  try {
    const user = await apiPost<AdminUserDto>("/api/admin/users", input, {
      bearerToken: session.accessToken,
    });
    return { ok: true, user };
  } catch (e) {
    const msg = e instanceof Error ? e.message : "";
    if (/409|conflict|taken|already|exist/i.test(msg))
      return { ok: false, error: "Nom d'utilisateur ou email déjà utilisé." };
    return { ok: false, error: msg || "Échec de la création." };
  }
}

export async function deleteAccountAction(id: number): Promise<AccountResult> {
  const session = await requireAdmin();
  if (!session) return { ok: false, error: "Réservé aux administrateurs." };
  try {
    await apiDelete(`/api/admin/users/${id}`, { bearerToken: session.accessToken });
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function setUserFlagsAction(
  id: number,
  flags: { isAdmin?: boolean; isBanned?: boolean },
): Promise<AccountResult> {
  const session = await requireAdmin();
  if (!session) return { ok: false, error: "Réservé aux administrateurs." };
  try {
    const user = await apiPut<AdminUserDto>(`/api/admin/users/${id}`, flags, {
      bearerToken: session.accessToken,
    });
    return { ok: true, user };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
