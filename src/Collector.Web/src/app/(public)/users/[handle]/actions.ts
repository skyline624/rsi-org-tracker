"use server";

import { getSession } from "@/lib/auth/session";
import { apiPost, apiPut, apiDelete } from "@/lib/api/client";

export interface NoteDto {
  id: number;
  trackedEntityId: number;
  authorApiUserId: number;
  authorUsername: string;
  body: string;
  createdAt: string;
  updatedAt: string;
}

export interface NoteActionResult {
  ok: boolean;
  note?: NoteDto;
  error?: string;
}

export async function createNoteAction(handle: string, body: string): Promise<NoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPost<NoteDto>(
      `/api/users/${encodeURIComponent(handle)}/notes`,
      { body: trimmed },
      { bearerToken: session.accessToken },
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function updateNoteAction(id: number, body: string): Promise<NoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPut<NoteDto>(
      `/api/notes/${id}`,
      { body: trimmed },
      { bearerToken: session.accessToken },
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteNoteAction(id: number): Promise<NoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  try {
    await apiDelete(`/api/notes/${id}`, { bearerToken: session.accessToken });
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
