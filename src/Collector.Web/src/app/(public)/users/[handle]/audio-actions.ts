"use server";

import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiDelete, apiUpload } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";

export interface AudioDto {
  id: number;
  trackedEntityId: number;
  authorApiUserId: number;
  authorUsername: string;
  originalName: string;
  mimeType: string;
  sizeBytes: number;
  durationSec: number | null;
  createdAt: string;
}

export interface AudioActionResult {
  ok: boolean;
  audio?: AudioDto;
  error?: string;
}

export async function uploadAudioAction(handle: string, formData: FormData): Promise<AudioActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };

  const file = formData.get("file");
  if (!(file instanceof File) || file.size === 0) return { ok: false, error: "Aucun fichier." };

  try {
    const apiForm = new FormData();
    apiForm.append("file", file);
    const audio = await apiUpload<AudioDto>(
      `/api/users/${encodeURIComponent(handle)}/audio`,
      apiForm,
      sessionCtx(session),
    );
    return { ok: true, audio };
  } catch (e) {
    if (e instanceof ApiError) {
      const message = (e.problem as { message?: string }).message;
      return { ok: false, error: message ?? e.problem.title ?? `Erreur ${e.status}` };
    }
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteAudioAction(id: number): Promise<AudioActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  try {
    await apiDelete(`/api/audio/${id}`, sessionCtx(session));
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
