"use server";

import { getSession } from "@/lib/auth/session";
import { apiPost, apiPut, apiDelete } from "@/lib/api/client";

export interface OrgNoteDto {
  id: number;
  orgSid: string;
  authorApiUserId: number;
  authorUsername: string;
  body: string;
  createdAt: string;
  updatedAt: string;
}

export interface OrgNoteActionResult {
  ok: boolean;
  note?: OrgNoteDto;
  error?: string;
}

export async function createOrgNoteAction(sid: string, body: string): Promise<OrgNoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPost<OrgNoteDto>(
      `/api/organizations/${encodeURIComponent(sid)}/notes`,
      { body: trimmed },
      { bearerToken: session.accessToken },
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function updateOrgNoteAction(id: number, body: string): Promise<OrgNoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPut<OrgNoteDto>(
      `/api/org-notes/${id}`,
      { body: trimmed },
      { bearerToken: session.accessToken },
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteOrgNoteAction(id: number): Promise<OrgNoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  try {
    await apiDelete(`/api/org-notes/${id}`, { bearerToken: session.accessToken });
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
