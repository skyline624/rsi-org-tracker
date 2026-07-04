"use server";

import { getSession } from "@/lib/auth/session";
import { apiPut } from "@/lib/api/client";

export interface TrackedEntityDto {
  id: number;
  citizenId: number | null;
  currentHandle: string | null;
  displayName: string | null;
  source: string;
  status: string;
  createdAt: string;
}

export interface SetCitizenIdResult {
  ok: boolean;
  entity?: TrackedEntityDto;
  error?: string;
}

export async function setCitizenIdAction(handle: string, citizenId: number): Promise<SetCitizenIdResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!Number.isInteger(citizenId) || citizenId < 1)
    return { ok: false, error: "Citizen id invalide (nombre positif attendu)." };
  try {
    const entity = await apiPut<TrackedEntityDto>(
      `/api/users/${encodeURIComponent(handle)}/citizen-id`,
      { citizenId },
      { bearerToken: session.accessToken },
    );
    return { ok: true, entity };
  } catch (e) {
    const msg = e instanceof Error ? e.message : "";
    if (/409|conflict|attribu/i.test(msg))
      return { ok: false, error: "Ce citizen id est déjà attribué à une autre personne." };
    return { ok: false, error: msg || "Échec de l'enregistrement." };
  }
}
