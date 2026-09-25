"use server";

import { INVALID_ARGUMENTS, idSchema, noteBodySchema, sidSchema, valid } from "@/lib/validation";
import { getSession, sessionCtx } from "@/lib/auth/session";
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
  if (!valid(sidSchema, sid) || typeof body !== "string") return { ok: false, error: INVALID_ARGUMENTS };
  if (!valid(noteBodySchema, body))
    return { ok: false, error: body.trim() ? "Note trop longue (10 000 caractères max)." : "La note est vide." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPost<OrgNoteDto>(
      `/api/organizations/${encodeURIComponent(sid)}/notes`,
      { body: trimmed },
      sessionCtx(session),
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function updateOrgNoteAction(id: number, body: string): Promise<OrgNoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!valid(idSchema, id) || typeof body !== "string") return { ok: false, error: INVALID_ARGUMENTS };
  if (!valid(noteBodySchema, body))
    return { ok: false, error: body.trim() ? "Note trop longue (10 000 caractères max)." : "La note est vide." };
  const trimmed = body.trim();
  if (!trimmed) return { ok: false, error: "La note est vide." };
  try {
    const note = await apiPut<OrgNoteDto>(
      `/api/org-notes/${id}`,
      { body: trimmed },
      sessionCtx(session),
    );
    return { ok: true, note };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteOrgNoteAction(id: number): Promise<OrgNoteActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!valid(idSchema, id)) return { ok: false, error: INVALID_ARGUMENTS };
  try {
    await apiDelete(`/api/org-notes/${id}`, sessionCtx(session));
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
