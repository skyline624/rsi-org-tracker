import { HudPanel } from "@/components/hud/HudPanel";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { TopOrgsTable } from "@/components/stats/TopOrgsTable";
import { TimelineChart } from "@/components/charts/TimelineChart";
import { ArchetypeDonut } from "@/components/charts/ArchetypeDonut";
import { MemberActivityBar } from "@/components/charts/MemberActivityBar";
import {
  getArchetypeStats,
  getMemberActivity,
  getStatsOverview,
  getStatsTimeline,
  getTopOrgs,
} from "@/lib/api/endpoints";
import { formatRelative } from "@/lib/utils/format";

export const revalidate = 120;

export default async function StatsPage() {
  // On tolère que certains endpoints échouent ; chaque panel gère son propre cas vide.
  const [overviewRes, timelineRes, topRes, archetypesRes, activityRes] =
    await Promise.allSettled([
      getStatsOverview({ serverSide: true }),
      getStatsTimeline(30, { serverSide: true }),
      getTopOrgs(10, { serverSide: true }),
      getArchetypeStats({ serverSide: true }),
      getMemberActivity(30, { serverSide: true }),
    ]);

  const overview = overviewRes.status === "fulfilled" ? overviewRes.value : null;
  const timeline = timelineRes.status === "fulfilled" ? timelineRes.value : [];
  const top = topRes.status === "fulfilled" ? topRes.value : [];
  const archetypes =
    archetypesRes.status === "fulfilled" ? archetypesRes.value : [];
  const activity = activityRes.status === "fulfilled" ? activityRes.value : [];

  const timelinePoints = timeline.map((p) => ({
    date: p.date,
    value: p.changeCount,
  }));

  return (
    <div className="flex flex-col gap-8">
      <header>
        <div className="hud-label">— UEE::GLOBAL_TELEMETRY</div>
        <h1 className="mt-1 font-display text-3xl">Stats Dashboard</h1>
      </header>

      {/* KPIs */}
      <section className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
        <HudStatTile
          label="Organizations"
          value={overview?.totalOrganizations ?? 0}
        />
        <HudStatTile label="Citizens" value={overview?.totalUsers ?? 0} />
        <HudStatTile
          label="Last Collection"
          value={
            overview?.lastCollectionAt
              ? formatRelative(overview.lastCollectionAt)
              : "—"
          }
          compact={false}
          accent="green"
        />
        <HudStatTile
          label="Change Events (30d)"
          value={timeline.reduce((a, p) => a + p.changeCount, 0)}
          accent="orange"
        />
      </section>

      {/* Timeline */}
      <section>
        <HudPanel label="CHANGE EVENTS · LAST 30 DAYS">
          {timelinePoints.length > 1 ? (
            <TimelineChart data={timelinePoints} label="events" />
          ) : (
            <div className="py-10 text-center font-mono text-xs text-hud-text-dim">
              — no timeline data —
            </div>
          )}
        </HudPanel>
      </section>

      {/* Donut + activity */}
      <section className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <HudPanel label="ORG ARCHETYPES DISTRIBUTION">
          {archetypes.length > 0 ? (
            <ArchetypeDonut data={archetypes} />
          ) : (
            <div className="py-10 text-center font-mono text-xs text-hud-text-dim">
              — endpoint not available —
            </div>
          )}
        </HudPanel>

        <HudPanel label="MEMBER JOINS / LEAVES · 30 DAYS" accent="orange">
          {activity.length > 0 ? (
            <MemberActivityBar data={activity} />
          ) : (
            <div className="py-10 text-center font-mono text-xs text-hud-text-dim">
              — endpoint not available —
            </div>
          )}
        </HudPanel>
      </section>

      {/* Top orgs */}
      <section>
        <HudPanel label="TOP ORGS BY HEADCOUNT">
          <TopOrgsTable rows={top} />
        </HudPanel>
      </section>
    </div>
  );
}
