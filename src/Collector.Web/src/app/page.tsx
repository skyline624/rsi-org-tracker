import Link from "next/link";
import { ChangesTable } from "@/components/changes/ChangesTable";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { HudButton } from "@/components/hud/HudButton";
import {
  getCycleStatus,
  getStatsOverview,
  listChanges,
} from "@/lib/api/endpoints";
import { formatRelative, formatNumber } from "@/lib/utils/format";
import { Activity, Database, Users, Radar } from "lucide-react";

// RSC — re-render every 60s via Next cache revalidation.
export const revalidate = 60;

async function fetchDashboardData() {
  // On tolère que certains appels échouent (l'API n'expose pas forcément tous
  // les endpoints stats encore) pour ne pas casser la home.
  const [overview, cycle, recentChanges] = await Promise.allSettled([
    getStatsOverview({ serverSide: true }),
    getCycleStatus({ serverSide: true }),
    listChanges({ limit: 100 }, { serverSide: true }),
  ]);

  return {
    overview: overview.status === "fulfilled" ? overview.value : null,
    cycle: cycle.status === "fulfilled" ? cycle.value : null,
    recent: recentChanges.status === "fulfilled" ? recentChanges.value : [],
  };
}

export default async function HomePage() {
  const { overview, cycle, recent } = await fetchDashboardData();

  return (
    <div className="flex flex-col gap-10">
      {/* Hero */}
      <section className="flex flex-col gap-4">
        <div className="hud-label">— UEE::CITIZEN_INTEL_NETWORK</div>
        <h1 className="max-w-3xl font-display text-4xl font-bold leading-tight md:text-6xl">
          Real-time intel on every{" "}
          <span className="text-hud-cyan">Star Citizen</span> organization.
        </h1>
        <p className="max-w-2xl font-ui text-base text-hud-text-dim md:text-lg">
          Track {formatNumber(overview?.totalOrganizations ?? 104_228)}{" "}
          registered orgs and{" "}
          {formatNumber(overview?.totalUsers ?? 411_465)} citizens.
          Join/leave events, rank changes, handle history, growth
          timelines — all pulled straight from RSI and indexed for you.
        </p>
        <div className="mt-2 flex flex-wrap gap-3">
          <Link href="/orgs">
            <HudButton variant="primary">
              <Radar className="h-3 w-3" /> Browse Organizations
            </HudButton>
          </Link>
          <Link href="/users">
            <HudButton variant="ghost">Search Citizens</HudButton>
          </Link>
          <Link href="/stats">
            <HudButton variant="ghost">View Stats</HudButton>
          </Link>
        </div>
      </section>

      {/* KPI tiles */}
      <section className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
        <HudStatTile
          label="Organizations"
          value={overview?.totalOrganizations ?? 0}
          sub="tracked"
          icon={<Database className="h-3 w-3" />}
          accent="cyan"
        />
        <HudStatTile
          label="Citizens"
          value={overview?.totalUsers ?? 0}
          sub="indexed"
          icon={<Users className="h-3 w-3" />}
          accent="cyan"
        />
        <HudStatTile
          label="Queue Pending"
          value={cycle?.queue_pending ?? 0}
          sub={
            cycle?.queue_stuck && cycle.queue_stuck > 0
              ? `${formatNumber(cycle.queue_stuck)} stuck`
              : "flowing"
          }
          icon={<Activity className="h-3 w-3" />}
          accent={
            cycle?.queue_stuck && cycle.queue_stuck > 0 ? "orange" : "green"
          }
        />
        <HudStatTile
          label="Last Collection"
          value={
            overview?.lastCollectionAt
              ? formatRelative(overview.lastCollectionAt)
              : "—"
          }
          sub={
            cycle?.last_member_collection
              ? `org: ${cycle.last_member_collection.org_sid}`
              : "waiting"
          }
          compact={false}
          accent="green"
        />
      </section>

      {/* Recent changelog */}
      <section className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <HudPanel label="LAST TRANSMISSIONS" className="lg:col-span-2">
          <ChangesTable rows={recent} />
        </HudPanel>

        <HudPanel label="COLLECTOR_STATUS" accent="green">
          <ul className="flex flex-col gap-3 font-mono text-xs">
            <li className="flex items-center justify-between">
              <span className="text-hud-text-dim">DISCOVERED</span>
              <span className="text-hud-cyan">
                {formatNumber(cycle?.discovered_orgs ?? 0)}
              </span>
            </li>
            <li className="flex items-center justify-between">
              <span className="text-hud-text-dim">QUEUE PENDING</span>
              <span className="text-hud-cyan">
                {formatNumber(cycle?.queue_pending ?? 0)}
              </span>
            </li>
            <li className="flex items-center justify-between">
              <span className="text-hud-text-dim">QUEUE STUCK</span>
              <span
                className={
                  (cycle?.queue_stuck ?? 0) > 0
                    ? "text-hud-orange"
                    : "text-hud-green"
                }
              >
                {formatNumber(cycle?.queue_stuck ?? 0)}
              </span>
            </li>
            <li className="flex items-center justify-between">
              <span className="text-hud-text-dim">LAST CYCLE</span>
              <span className="text-hud-cyan">
                {cycle?.last_member_collection
                  ? formatRelative(cycle.last_member_collection.at)
                  : "—"}
              </span>
            </li>
          </ul>
        </HudPanel>
      </section>
    </div>
  );
}
