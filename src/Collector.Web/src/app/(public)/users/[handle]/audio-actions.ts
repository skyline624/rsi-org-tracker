"use server";

import { getSession } from "@/lib/auth/session";
import { apiDelete } from "@/lib/api/client";

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
    const res = await fetch(
      `${process.env.API_BASE_URL}/api/users/${encodeURIComponent(handle)}/audio`,
      {
        method: "POST",
        headers: { Authorization: `Bearer ${session.accessToken}` },
        body: apiForm,
        cache: "no-store",
      },
    );
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      return { ok: false, error: body.message ?? body.title ?? `Erreur ${res.status}` };
    }
    const audio = (await res.json()) as AudioDto;
    return { ok: true, audio };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteAudioAction(id: number): Promise<AudioActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  try {
    await apiDelete(`/api/audio/${id}`, { bearerToken: session.accessToken });
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
