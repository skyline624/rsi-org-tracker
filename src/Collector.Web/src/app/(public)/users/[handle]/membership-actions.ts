"use server";

import { getSession } from "@/lib/auth/session";
import { apiGet, apiPost, apiDelete } from "@/lib/api/client";

export interface OrgOption {
  sid: string;
  name: string;
}

/** Autocomplete search over known organizations (collected or manually added). */
export async function searchOrgsAction(query: string): Promise<OrgOption[]> {
  const session = await getSession();
  const q = query.trim();
  if (!session || q.length < 1) return [];
  try {
    const res = await apiGet<{ items: Array<{ sid: string; name: string }> }>(
      "/api/organizations",
      { search: q, pageSize: 10 },
      { bearerToken: session.accessToken },
    );
    return (res.items ?? []).map((o) => ({ sid: o.sid, name: o.name }));
  } catch {
    return [];
  }
}

export interface MembershipDto {
  id: number;
  orgSid: string;
  orgName: string | null;
  rank: string | null;
  via: string;
  sinceDate: string;
  authorUsername: string;
  createdAt: string;
}

export interface MembershipActionResult {
  ok: boolean;
  membership?: MembershipDto;
  error?: string;
}

export async function createMembershipAction(
  handle: string,
  input: { orgSid: string; rank?: string; via?: string; sinceDate?: string },
): Promise<MembershipActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  if (!input.orgSid.trim()) return { ok: false, error: "SID de l'organisation requis." };

  try {
    const membership = await apiPost<MembershipDto>(
      `/api/users/${encodeURIComponent(handle)}/memberships`,
      {
        orgSid: input.orgSid.trim(),
        rank: input.rank?.trim() || null,
        via: input.via || null,
        sinceDate: input.sinceDate || null,
      },
      { bearerToken: session.accessToken },
    );
    return { ok: true, membership };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}

export async function deleteMembershipAction(id: number): Promise<MembershipActionResult> {
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  try {
    await apiDelete(`/api/memberships/${id}`, { bearerToken: session.accessToken });
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec." };
  }
}
