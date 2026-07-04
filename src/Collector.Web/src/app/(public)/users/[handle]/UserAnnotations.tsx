import { getSession } from "@/lib/auth/session";
import { apiGet } from "@/lib/api/client";
import { CitizenIdPanel } from "./CitizenIdPanel";
import { UexPanel } from "./UexPanel";
import { DiscordPanel, type DiscordProfile } from "./DiscordPanel";
import { ExternalLinksPanel } from "./ExternalLinksPanel";
import { NotesSection } from "./NotesSection";
import { AudioSection } from "./AudioSection";
import { MembershipsSection } from "./MembershipsSection";
import type { NoteDto } from "./actions";
import type { AudioDto } from "./audio-actions";
import type { MembershipDto } from "./membership-actions";
import type { LinkDto } from "./link-actions";

/**
 * Notes, audio recordings, manual org memberships, UEX/Discord profiles and
 * external links for a person. Works for enriched AND roster-only (no citizen
 * id) profiles alike, since everything attaches to the stable internal entity
 * resolved from the handle.
 *
 * - canSetCitizenId (roster-only view): also exposes a panel to assign a citizen id.
 * - Discord panel: enriched from the manually-added Discord id via our backend.
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
  const [notes, audio, memberships, links, entity] = await Promise.all([
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
    apiGet<LinkDto[]>(`/api/users/${encodeURIComponent(handle)}/links`, undefined, opts).catch(
      () => [] as LinkDto[],
    ),
    canSetCitizenId
      ? apiGet<{ citizenId: number | null } | null>(
          `/api/users/${encodeURIComponent(handle)}/entity`,
          undefined,
          opts,
        ).catch(() => null)
      : Promise.resolve(null),
  ]);

  // Enrich each manually-added Discord id (a person may have several accounts).
  const discordIds = links.filter((l) => l.provider === "discord").map((l) => l.value);
  const discordProfiles = (
    await Promise.all(
      discordIds.map((id) =>
        apiGet<DiscordProfile>(
          `/api/discord/users/${encodeURIComponent(id)}`,
          undefined,
          opts,
        ).catch(() => null),
      ),
    )
  ).filter((p): p is DiscordProfile => p !== null);

  return (
    <>
      {canSetCitizenId && (
        <CitizenIdPanel handle={handle} initialCitizenId={entity?.citizenId ?? null} />
      )}
      <UexPanel handle={handle} />
      {discordProfiles.length > 0 && <DiscordPanel profiles={discordProfiles} />}
      <ExternalLinksPanel
        key={links.map((l) => l.id).join(",")}
        handle={handle}
        initialLinks={links}
        currentUsername={session.username}
        isAdmin={session.isAdmin}
      />
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
