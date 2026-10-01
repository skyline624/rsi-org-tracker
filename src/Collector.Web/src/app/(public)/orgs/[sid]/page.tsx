import Image from "next/image";
import { notFound } from "next/navigation";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { OrgMembersTable } from "@/components/org/OrgMembersTable";
import { TimelineChart } from "@/components/charts/lazy";
import {
  getOrg,
  getOrgDiscordGuilds,
  getOrgGrowth,
  getOrgMemberChanges,
  getOrgMembersPage,
} from "@/lib/api/endpoints";
import { ApiError } from "@/lib/api/errors";
import type { DiscordOrgGuildDto, OrganizationMemberDto, PaginatedResponse } from "@/lib/api/types";
import { formatDate, formatNumber, formatRelative } from "@/lib/utils/format";
import { lastChecked } from "@/lib/utils/last-checked";
import { getSession } from "@/lib/auth/session";
import { requireAuthCtx, withAuthRedirect } from "@/lib/auth/server-api";
import { apiGet } from "@/lib/api/client";
import { Pagination } from "@/components/layout/Pagination";
import { parsePage } from "@/lib/utils/page-param";
import { OrgNotesSection } from "./OrgNotesSection";
import { QuickAddMember } from "./QuickAddMember";
import { ManualMembersPanel, type OrgManualMember } from "./ManualMembersPanel";
import { OrgDiscordPanel } from "./OrgDiscordPanel";
import type { OrgNoteDto } from "./org-note-actions";

export const dynamic = "force-dynamic";

interface PageProps {
  params: Promise<{ sid: string }>;
  searchParams: Promise<{ members?: string; former?: string }>;
}

/** Rosters are paginated by the API (TEST has 24 000 members). */
const MEMBERS_PAGE_SIZE = 50;

const noMembers = (page: number): PaginatedResponse<OrganizationMemberDto> => ({
  items: [],
  total: 0,
  page,
  pageSize: MEMBERS_PAGE_SIZE,
  totalPages: 0,
});

export default async function OrgDetailPage({ params, searchParams }: PageProps) {
  const [{ sid }, sp] = await Promise.all([params, searchParams]);
  const ctx = await requireAuthCtx();
  const activePage = parsePage(sp.members);
  const formerPage = parsePage(sp.former);

  let org;
  try {
    org = await withAuthRedirect(getOrg(sid, ctx));
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  // Everything else at once: each block degrades to empty rather than failing the page.
  const orgPath = `/api/organizations/${encodeURIComponent(sid)}`;
  const [session, members, formerMembers, growth, changes, notes, manualMembers, discordGuilds] =
    await Promise.all([
      getSession(),
      getOrgMembersPage(sid, { status: "active", page: activePage, pageSize: MEMBERS_PAGE_SIZE }, ctx)
        .catch(() => noMembers(activePage)),
      getOrgMembersPage(sid, { status: "former", page: formerPage, pageSize: MEMBERS_PAGE_SIZE }, ctx)
        .catch(() => noMembers(formerPage)),
      getOrgGrowth(sid, ctx).catch(() => []),
      getOrgMemberChanges(sid, 20, ctx).catch(() => []),
      apiGet<OrgNoteDto[]>(`${orgPath}/notes`, undefined, ctx).catch(() => [] as OrgNoteDto[]),
      apiGet<OrgManualMember[]>(`${orgPath}/manual-members`, undefined, ctx)
        .catch(() => [] as OrgManualMember[]),
      // Discord servers mapped to this org; the panel stays hidden when there is none.
      getOrgDiscordGuilds(ctx, sid).catch(() => [] as DiscordOrgGuildDto[]),
    ]);

  const chartData = growth.map((g) => ({
    date: g.date,
    value: g.membersCount,
  }));

  return (
    <div className="flex flex-col gap-8">
      {/* Header banner */}
      <section className="relative overflow-hidden border-b border-hud-cyan/30 pb-6">
        <div className="flex items-start gap-6">
          {org.urlImage && (
            <div className="hud-clip relative h-28 w-28 shrink-0 border border-hud-cyan/40 bg-hud-bg-elevated">
              <Image
                src={
                  org.urlImage.startsWith("http")
                    ? org.urlImage
                    : `https://robertsspaceindustries.com${org.urlImage}`
                }
                alt={org.name}
                fill
                className="object-cover"
              />
            </div>
          )}
          <div className="flex-1">
            <div className="hud-label">— UEE::ORG_PROFILE</div>
            <h1 className="mt-1 font-display text-4xl font-bold">
              {org.name}
            </h1>
            <div className="mt-2 flex items-center gap-2 font-mono text-xs text-hud-text-dim">
              <span className="text-hud-cyan">[{org.sid}]</span>
              {org.archetype && <HudBadge tone="cyan">{org.archetype}</HudBadge>}
              {org.lang && <HudBadge tone="dim">{org.lang}</HudBadge>}
              {org.recruiting && <HudBadge tone="green">RECRUITING</HudBadge>}
              {org.roleplay && <HudBadge tone="orange">ROLEPLAY</HudBadge>}
              <span>· LAST SYNC {formatRelative(lastChecked(org))}</span>
            </div>
            {org.description && (
              <p className="mt-4 max-w-3xl font-ui text-sm leading-relaxed text-hud-text">
                {org.description}
              </p>
            )}
          </div>
        </div>
      </section>

      {/* KPIs */}
      <section className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <HudStatTile label="Members" value={org.membersCount} accent="cyan" />
        <HudStatTile
          label="Last checked"
          value={formatDate(lastChecked(org))}
          sub={`last change ${formatRelative(org.timestamp)}`}
          compact={false}
          accent="green"
        />
        <HudStatTile
          label="Primary Focus"
          value={org.focusPrimaryName ?? "—"}
          compact={false}
          accent="cyan"
        />
        <HudStatTile
          label="Changes Tracked"
          value={changes.length}
          sub="last 20"
          accent="orange"
        />
      </section>

      {/* Growth */}
      <section>
        <HudPanel label="MEMBER GROWTH TIMELINE">
          {chartData.length > 1 ? (
            <TimelineChart data={chartData} label="members" />
          ) : (
            <div className="py-10 text-center font-mono text-xs text-hud-text-dim">
              — not enough history yet —
            </div>
          )}
        </HudPanel>
      </section>

      <QuickAddMember sid={sid} />

      <ManualMembersPanel members={manualMembers} />

      <OrgDiscordPanel guilds={discordGuilds} />

      {/* Members + Changes */}
      <section className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <HudPanel
          label={`ACTIVE ROSTER · ${formatNumber(members.total)}`}
          className="xl:col-span-2"
        >
          <OrgMembersTable rows={members.items} empty="No active members in snapshot." />
          {members.totalPages > 1 && (
            <Pagination
              param="members"
              page={members.page}
              totalPages={members.totalPages}
              total={members.total}
            />
          )}
        </HudPanel>

        <HudPanel label="RECENT ACTIVITY" accent="orange">
          {changes.length === 0 ? (
            <div className="py-6 text-center font-mono text-xs text-hud-text-dim">
              — no activity —
            </div>
          ) : (
            <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
              {changes.map((c) => (
                <li key={c.id} className="py-2">
                  <div className="flex items-center gap-2">
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
                    <span className="truncate text-hud-cyan">
                      {c.userHandle ?? c.entityId}
                    </span>
                  </div>
                  <div className="mt-1 text-[10px] text-hud-text-dim">
                    {formatRelative(c.timestamp)}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </HudPanel>
      </section>

      {/* Former members — only rendered when some exist. Uses the same
          OrgMembersTable component, with a "red" accent to signal the
          roster is historical (left the org / org was gone last seen). */}
      {formerMembers.total > 0 && (
        <section>
          <HudPanel
            label={`FORMER MEMBERS · ${formatNumber(formerMembers.total)}`}
            accent="red"
          >
            <p className="mb-3 font-mono text-[10px] uppercase tracking-wider text-hud-text-dim">
              — citizens previously tracked in this org who left or were
              purged when the org returned empty —
            </p>
            <OrgMembersTable rows={formerMembers.items} />
            {formerMembers.totalPages > 1 && (
              <Pagination
                param="former"
                page={formerMembers.page}
                totalPages={formerMembers.totalPages}
                total={formerMembers.total}
              />
            )}
          </HudPanel>
        </section>
      )}

      <OrgNotesSection
        sid={sid}
        initialNotes={notes}
        currentUserId={session?.userId}
        isAdmin={session?.isAdmin}
      />
    </div>
  );
}
