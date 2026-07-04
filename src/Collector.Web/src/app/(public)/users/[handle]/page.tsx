import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { PartialUserProfile } from "@/components/user/PartialUserProfile";
import { UserOrgsTable } from "@/components/user/UserOrgsTable";
import {
  getUser,
  getUserChanges,
  getUserHandleHistory,
  getUserOrgs,
} from "@/lib/api/endpoints";
import { ApiError } from "@/lib/api/errors";
import type { OrganizationMemberDto } from "@/lib/api/types";
import { formatDate, formatNumber, formatRelative } from "@/lib/utils/format";
import { getSession } from "@/lib/auth/session";
import { apiGet } from "@/lib/api/client";
import { NotesSection } from "./NotesSection";
import { AudioSection } from "./AudioSection";
import { MembershipsSection } from "./MembershipsSection";
import type { NoteDto } from "./actions";
import type { AudioDto } from "./audio-actions";
import type { MembershipDto } from "./membership-actions";

export const dynamic = "force-dynamic";

interface PageProps {
  params: Promise<{ handle: string }>;
}

export default async function UserDetailPage({ params }: PageProps) {
  const { handle } = await params;

  let user;
  try {
    user = await getUser(handle, { serverSide: true });
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      // Profile not enriched (or wrongly stored under a parsed-out handle) —
      // fall back to partial view if the handle is known anywhere in
      // organization_members, including inactive (former) memberships.
      const knownOrgs = await getUserOrgs(handle, true, {
        serverSide: true,
      }).catch(() => [] as OrganizationMemberDto[]);
      if (knownOrgs.length > 0) {
        return <PartialUserProfile handle={handle} orgs={knownOrgs} />;
      }
      notFound();
    }
    throw err;
  }

  const [allOrgs, history, rawChanges] = await Promise.all([
    // include_inactive=true: surface former memberships too (e.g. orgs the
    // citizen left). UserOrgsTable shows a STATUS column to distinguish them.
    getUserOrgs(handle, true, { serverSide: true }).catch(
      () => [] as OrganizationMemberDto[],
    ),
    getUserHandleHistory(handle, { serverSide: true }).catch(() => []),
    getUserChanges(handle, 50, { serverSide: true }).catch(() => []),
  ]);

  const activeCount = allOrgs.filter((o) => o.isActive).length;
  const formerCount = allOrgs.length - activeCount;

  // Drop residual handle_changed events left over from the "CITIZEN DOSSIER"
  // parser regression. They're cosmetic noise once the row is repaired.
  const changes = rawChanges.filter(
    (c) =>
      c.oldValue !== "CITIZEN DOSSIER" && c.newValue !== "CITIZEN DOSSIER",
  );

  // Notes attached to this citizen (auth required — empty if not logged in).
  const session = await getSession();
  const notes = session
    ? await apiGet<NoteDto[]>(
        `/api/users/${encodeURIComponent(handle)}/notes`,
        undefined,
        { bearerToken: session.accessToken },
      ).catch(() => [] as NoteDto[])
    : [];
  const audio = session
    ? await apiGet<AudioDto[]>(
        `/api/users/${encodeURIComponent(handle)}/audio`,
        undefined,
        { bearerToken: session.accessToken },
      ).catch(() => [] as AudioDto[])
    : [];
  const memberships = session
    ? await apiGet<MembershipDto[]>(
        `/api/users/${encodeURIComponent(handle)}/memberships`,
        undefined,
        { bearerToken: session.accessToken },
      ).catch(() => [] as MembershipDto[])
    : [];

  return (
    <div className="flex flex-col gap-8">
      {/* Header */}
      <section className="border-b border-hud-cyan/30 pb-6">
        <div className="flex items-start gap-6">
          {user.urlImage && (
            <div className="hud-clip relative h-28 w-28 shrink-0 border border-hud-cyan/40 bg-hud-bg-elevated">
              <Image
                src={
                  user.urlImage.startsWith("http")
                    ? user.urlImage
                    : `https://robertsspaceindustries.com${user.urlImage}`
                }
                alt={user.userHandle}
                fill
                className="object-cover"
                unoptimized
              />
            </div>
          )}
          <div className="flex-1">
            <div className="hud-label">— UEE::CITIZEN_RECORD</div>
            <h1 className="mt-1 font-display text-4xl font-bold">
              {user.displayName ?? user.userHandle}
            </h1>
            <div className="mt-2 flex flex-wrap items-center gap-2 font-mono text-xs text-hud-text-dim">
              <span className="text-hud-cyan">@{user.userHandle}</span>
              <HudBadge tone="dim">#{formatNumber(user.citizenId)}</HudBadge>
              {user.location && (
                <HudBadge tone="cyan">{user.location}</HudBadge>
              )}
              {user.enlisted && (
                <span>· ENLISTED {formatDate(user.enlisted)}</span>
              )}
              <span>· LAST SYNC {formatRelative(user.updatedAt)}</span>
            </div>
            {user.bio && (
              <p className="mt-4 max-w-3xl font-ui text-sm leading-relaxed text-hud-text">
                {user.bio}
              </p>
            )}
          </div>
        </div>
      </section>

      {/* KPIs */}
      <section className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <HudStatTile
          label="Orgs"
          value={allOrgs.length}
          sub={`${activeCount} active · ${formerCount} former`}
          accent="cyan"
        />
        <HudStatTile
          label="Handle history"
          value={history.length}
          sub={history.length > 1 ? "renamed" : "stable"}
          accent={history.length > 1 ? "orange" : "green"}
        />
        <HudStatTile
          label="Changes tracked"
          value={changes.length}
          sub="last 50"
          accent="orange"
        />
        <HudStatTile
          label="Indexed"
          value={formatRelative(user.updatedAt)}
          compact={false}
          accent="green"
        />
      </section>

      {/* Orgs + history + changes */}
      <section className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <HudPanel label="ORG MEMBERSHIPS" className="xl:col-span-2">
          <UserOrgsTable rows={allOrgs} />
        </HudPanel>

        <HudPanel label="HANDLE TIMELINE" accent="orange">
          {history.length === 0 ? (
            <div className="py-6 text-center font-mono text-xs text-hud-text-dim">
              — no history —
            </div>
          ) : (
            <ul className="flex flex-col gap-3 font-mono text-xs">
              {history.map((h) => (
                <li
                  key={`${h.userHandle}-${h.firstSeen}`}
                  className="flex items-center justify-between"
                >
                  <span className="text-hud-cyan">@{h.userHandle}</span>
                  <span className="text-hud-text-dim">
                    {formatDate(h.firstSeen)}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </HudPanel>
      </section>

      <section>
        <HudPanel label="CHANGE TRANSMISSIONS" accent="cyan">
          {changes.length === 0 ? (
            <div className="py-6 text-center font-mono text-xs text-hud-text-dim">
              — no transmissions —
            </div>
          ) : (
            <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
              {changes.map((c) => (
                <li
                  key={c.id}
                  className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2"
                >
                  <div className="flex flex-wrap items-center gap-3">
                    <HudBadge
                      tone={
                        c.changeType.includes("joined")
                          ? "green"
                          : c.changeType.includes("left")
                            ? "red"
                            : "orange"
                      }
                    >
                      {c.changeType.replace(/_/g, " ")}
                    </HudBadge>
                    {c.orgSid && (
                      <Link
                        href={`/orgs/${c.orgSid}`}
                        className="text-hud-cyan hover:text-hud-orange"
                      >
                        {c.orgSid}
                      </Link>
                    )}
                    {(c.oldValue || c.newValue) && (
                      <ChangeDiff
                        type={c.changeType}
                        oldValue={c.oldValue}
                        newValue={c.newValue}
                      />
                    )}
                  </div>
                  <time className="text-hud-text-dim">
                    {formatRelative(c.timestamp)}
                  </time>
                </li>
              ))}
            </ul>
          )}
        </HudPanel>
      </section>

      <NotesSection
        handle={handle}
        initialNotes={notes}
        currentUserId={session?.userId}
        isAdmin={session?.isAdmin}
      />

      <AudioSection
        handle={handle}
        initialAudio={audio}
        currentUserId={session?.userId}
        isAdmin={session?.isAdmin}
      />

      <MembershipsSection
        handle={handle}
        initialMemberships={memberships}
        currentUsername={session?.username}
        isAdmin={session?.isAdmin}
      />
    </div>
  );
}

/**
 * Render the old → new diff for the change types that actually carry one.
 * Skip the noisy serialized blobs (roles_changed stores a stringified array of
 * RSI's whitespace-laden role labels, which is unreadable inline).
 */
function ChangeDiff({
  type,
  oldValue,
  newValue,
}: {
  type: string;
  oldValue?: string | null;
  newValue?: string | null;
}) {
  if (type === "roles_changed") return null;

  const clean = (v: string | null | undefined) =>
    (v ?? "").trim().replace(/\s+/g, " ").slice(0, 80) || "—";

  return (
    <span className="flex items-center gap-2 text-hud-text-dim">
      <span className="text-hud-red/80 line-through">{clean(oldValue)}</span>
      <span aria-hidden>→</span>
      <span className="text-hud-green">{clean(newValue)}</span>
    </span>
  );
}
