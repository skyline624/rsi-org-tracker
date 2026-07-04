"use server";

import { getSession } from "@/lib/auth/session";
import { apiPost } from "@/lib/api/client";

export interface ActionResult {
  ok: boolean;
  error?: string;
}

/** Manually create a tracked person (redacted / roster-only). Admin only. */
export async function createEntityAction(input: {
  handle?: string;
  displayName?: string;
  citizenId?: number;
}): Promise<ActionResult> {
  const session = await getSession();
  if (!session?.isAdmin) return { ok: false, error: "Accès réservé aux administrateurs." };

  try {
    await apiPost(
      "/api/admin/entities",
      {
        handle: input.handle ?? null,
        displayName: input.displayName ?? null,
        citizenId: input.citizenId ?? null,
      },
      { bearerToken: session.accessToken },
    );
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec de la requête." };
  }
}

/** Manually create an organization (private / undiscovered). Admin only. */
export async function createOrganizationAction(input: {
  sid: string;
  name: string;
  urlImage?: string;
  archetype?: string;
  description?: string;
}): Promise<ActionResult> {
  const session = await getSession();
  if (!session?.isAdmin) return { ok: false, error: "Accès réservé aux administrateurs." };

  try {
    await apiPost(
      "/api/admin/organizations",
      {
        sid: input.sid,
        name: input.name,
        urlImage: input.urlImage ?? null,
        archetype: input.archetype ?? null,
        description: input.description ?? null,
      },
      { bearerToken: session.accessToken },
    );
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec de la requête." };
  }
}
