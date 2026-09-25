"use server";

import { INVALID_ARGUMENTS, handleSchema, idSchema, noteBodySchema, valid } from "@/lib/validation";
import { getSession, sessionCtx } from "@/lib/auth/session";
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
  if (!valid(handleSchema, handle) || typeof body !== "string") return { ok: false, error: INVALID_ARGUMENTS };
  if (!valid(noteBodySchema, body))
    return { ok: false, error: body.trim() ? "Note trop longue (10 000 caractères max)." : "La note est vide." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPost<NoteDto>(
      `/api/users/${encodeURIComponent(handle)}/notes`,
      { body: trimmed },
      sessionCtx(session),
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function updateNoteAction(id: number, body: string): Promise<NoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!valid(idSchema, id) || typeof body !== "string") return { ok: false, error: INVALID_ARGUMENTS };
  if (!valid(noteBodySchema, body))
    return { ok: false, error: body.trim() ? "Note trop longue (10 000 caractères max)." : "La note est vide." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPut<NoteDto>(
      `/api/notes/${id}`,
      { body: trimmed },
      sessionCtx(session),
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteNoteAction(id: number): Promise<NoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!valid(idSchema, id)) return { ok: false, error: INVALID_ARGUMENTS };
  try {
    await apiDelete(`/api/notes/${id}`, sessionCtx(session));
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
