import Image from "next/image";
import { notFound } from "next/navigation";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { OrgMembersTable } from "@/components/org/OrgMembersTable";
import { TimelineChart } from "@/components/charts/TimelineChart";
import {
  getOrg,
  getOrgGrowth,
  getOrgMemberChanges,
  getOrgMembers,
} from "@/lib/api/endpoints";
import { ApiError } from "@/lib/api/errors";
import type { OrganizationMemberDto } from "@/lib/api/types";
import { formatDate, formatNumber, formatRelative } from "@/lib/utils/format";

export const dynamic = "force-dynamic";

interface PageProps {
  params: Promise<{ sid: string }>;
}

export default async function OrgDetailPage({ params }: PageProps) {
  const { sid } = await params;

  let org;
  try {
    org = await getOrg(sid, { serverSide: true });
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }

  // Fetch the full roster in one shot (active + former) and split client-side.
  // The API returns the latest snapshot per member with the IsActive flag so we
  // know whether they're still in the org or have since left.
  const [allMembers, growth, changes] = await Promise.all([
    getOrgMembers(sid, { include_inactive: true }, { serverSide: true }).catch(
      () => [] as OrganizationMemberDto[],
    ),
    getOrgGrowth(sid, { serverSide: true }).catch(() => []),
    getOrgMemberChanges(sid, 20, { serverSide: true }).catch(() => []),
  ]);

  const members = allMembers.filter((m) => m.isActive);
  const formerMembers = allMembers.filter((m) => !m.isActive);

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
                unoptimized
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
              <span>· LAST SYNC {formatRelative(org.timestamp)}</span>
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
          label="Indexed at"
          value={formatDate(org.timestamp)}
          sub={formatRelative(org.timestamp)}
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

      {/* Members + Changes */}
      <section className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <HudPanel
          label={`ACTIVE ROSTER · ${formatNumber(members.length)}`}
          className="xl:col-span-2"
        >
          <OrgMembersTable rows={members} />
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
      {formerMembers.length > 0 && (
        <section>
          <HudPanel
            label={`FORMER MEMBERS · ${formatNumber(formerMembers.length)}`}
            accent="red"
          >
            <p className="mb-3 font-mono text-[10px] uppercase tracking-wider text-hud-text-dim">
              — citizens previously tracked in this org who left or were
              purged when the org returned empty —
            </p>
            <OrgMembersTable rows={formerMembers} />
          </HudPanel>
        </section>
      )}
    </div>
  );
}
