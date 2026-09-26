"use server";

import { INVALID_ARGUMENTS, entityInputSchema, organizationInputSchema, valid } from "@/lib/validation";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiPost } from "@/lib/api/client";

export interface ActionResult {
  ok: boolean;
  error?: string;
}

/** Manually create a tracked person (redacted / roster-only). Open to every signed-in account (see AdminDataController). */
export async function createEntityAction(input: {
  handle?: string;
  displayName?: string;
  citizenId?: number;
}): Promise<ActionResult> {
  if (!valid(entityInputSchema, input)) return { ok: false, error: INVALID_ARGUMENTS };
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };

  try {
    await apiPost(
      "/api/admin/entities",
      {
        handle: input.handle ?? null,
        displayName: input.displayName ?? null,
        citizenId: input.citizenId ?? null,
      },
      sessionCtx(session),
    );
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec de la requête." };
  }
}

/** Manually create an organization (private / undiscovered). Open to every signed-in account (see AdminDataController). */
export async function createOrganizationAction(input: {
  sid: string;
  name: string;
  urlImage?: string;
  archetype?: string;
  description?: string;
}): Promise<ActionResult> {
  if (!valid(organizationInputSchema, input)) return { ok: false, error: INVALID_ARGUMENTS };
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };

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
      sessionCtx(session),
    );
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec de la requête." };
  }
}
