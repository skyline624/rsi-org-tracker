import { getSession } from "@/lib/auth/session";
import { apiGet } from "@/lib/api/client";
import { CitizenIdPanel } from "./CitizenIdPanel";
import { NotesSection } from "./NotesSection";
import { AudioSection } from "./AudioSection";
import { MembershipsSection } from "./MembershipsSection";
import type { NoteDto } from "./actions";
import type { AudioDto } from "./audio-actions";
import type { MembershipDto } from "./membership-actions";

/**
 * Notes, audio recordings and manual org memberships for a person. Works for
 * enriched AND roster-only (no citizen id) profiles alike, since everything
 * attaches to the stable internal entity resolved from the handle.
 *
 * When canSetCitizenId is true (roster-only view), also exposes a panel to assign
 * a citizen id to a person who has none.
 */
export async function UserAnnotations({
  handle,
  canSetCitizenId,
}: {
  handle: string;
  canSetCitizenId?: boolean;
}) {
  const session = await getSession();
  if (!session) return null;

  const opts = { bearerToken: session.accessToken };
  const [notes, audio, memberships, entity] = await Promise.all([
    apiGet<NoteDto[]>(`/api/users/${encodeURIComponent(handle)}/notes`, undefined, opts).catch(
      () => [] as NoteDto[],
    ),
    apiGet<AudioDto[]>(`/api/users/${encodeURIComponent(handle)}/audio`, undefined, opts).catch(
      () => [] as AudioDto[],
    ),
    apiGet<MembershipDto[]>(
      `/api/users/${encodeURIComponent(handle)}/memberships`,
      undefined,
      opts,
    ).catch(() => [] as MembershipDto[]),
    canSetCitizenId
      ? apiGet<{ citizenId: number | null } | null>(
          `/api/users/${encodeURIComponent(handle)}/entity`,
          undefined,
          opts,
        ).catch(() => null)
      : Promise.resolve(null),
  ]);

  return (
    <>
      {canSetCitizenId && (
        <CitizenIdPanel handle={handle} initialCitizenId={entity?.citizenId ?? null} />
      )}
      <NotesSection
        handle={handle}
        initialNotes={notes}
        currentUserId={session.userId}
        isAdmin={session.isAdmin}
      />
      <AudioSection
        handle={handle}
        initialAudio={audio}
        currentUserId={session.userId}
        isAdmin={session.isAdmin}
      />
      <MembershipsSection
        handle={handle}
        initialMemberships={memberships}
        currentUsername={session.username}
        isAdmin={session.isAdmin}
      />
    </>
  );
}
