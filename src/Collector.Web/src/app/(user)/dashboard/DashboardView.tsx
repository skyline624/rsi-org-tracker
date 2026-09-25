"use client";
import Link from "next/link";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { HudBadge } from "@/components/hud/HudBadge";
import type { CycleStatusDto, StatsOverviewDto } from "@/lib/api/types";
import { useFavorites } from "@/lib/hooks/useFavorites";
import { formatNumber, formatRelative } from "@/lib/utils/format";

interface DashboardViewProps {
  overview: StatsOverviewDto | null;
  cycle: CycleStatusDto | null;
}

export function DashboardView({ overview, cycle }: DashboardViewProps) {
  const orgFavs = useFavorites((s) => s.orgs);
  const userFavs = useFavorites((s) => s.users);


  return (
    <div className="flex flex-col gap-8">
      {/* Overview tiles */}
      <section className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <HudStatTile
          label="Favorite Orgs"
          value={orgFavs.length}
          accent="cyan"
        />
        <HudStatTile
          label="Favorite Citizens"
          value={userFavs.length}
          accent="cyan"
        />
        <HudStatTile
          label="Orgs Tracked"
          value={overview?.totalOrganizations ?? 0}
          accent="green"
        />
        <HudStatTile
          label="Queue Pending"
          value={cycle?.queue_pending ?? 0}
          accent={
            (cycle?.queue_stuck ?? 0) > 0 ? "orange" : "green"
          }
          sub={
            cycle?.queue_stuck && cycle.queue_stuck > 0
              ? `${formatNumber(cycle.queue_stuck)} stuck`
              : "flowing"
          }
        />
      </section>

      <section className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <HudPanel label="YOUR FAVORITE ORGS">
          {orgFavs.length === 0 ? (
            <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
              — no favorites yet. Add some from{" "}
              <Link href="/orgs" className="text-hud-cyan">
                /orgs
              </Link>{" "}
              —
            </p>
          ) : (
            <ul className="flex flex-col gap-2 font-mono text-xs">
              {orgFavs.map((sid) => (
                <li key={sid} className="flex items-center justify-between">
                  <Link
                    href={`/orgs/${sid}`}
                    className="text-hud-cyan hover:text-hud-orange"
                  >
                    {sid}
                  </Link>
                  <HudBadge tone="dim">ORG</HudBadge>
                </li>
              ))}
            </ul>
          )}
        </HudPanel>

        <HudPanel label="YOUR FAVORITE CITIZENS">
          {userFavs.length === 0 ? (
            <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
              — no favorites yet. Add some from{" "}
              <Link href="/users" className="text-hud-cyan">
                /users
              </Link>{" "}
              —
            </p>
          ) : (
            <ul className="flex flex-col gap-2 font-mono text-xs">
              {userFavs.map((h) => (
                <li key={h} className="flex items-center justify-between">
                  <Link
                    href={`/users/${h}`}
                    className="text-hud-cyan hover:text-hud-orange"
                  >
                    @{h}
                  </Link>
                  <HudBadge tone="dim">USER</HudBadge>
                </li>
              ))}
            </ul>
          )}
        </HudPanel>
      </section>

      <section>
        <HudPanel label="COLLECTOR STATUS" accent="green">
          <ul className="grid grid-cols-2 gap-3 font-mono text-xs md:grid-cols-4">
            <li>
              <div className="text-hud-text-dim">LAST COLLECTION</div>
              <div className="mt-1 text-hud-cyan">
                {overview?.lastCollectionAt
                  ? formatRelative(overview.lastCollectionAt)
                  : "—"}
              </div>
            </li>
            <li>
              <div className="text-hud-text-dim">DISCOVERED ORGS</div>
              <div className="mt-1 text-hud-cyan">
                {formatNumber(cycle?.discovered_orgs ?? 0)}
              </div>
            </li>
            <li>
              <div className="text-hud-text-dim">LAST ORG COLLECTED</div>
              <div className="mt-1 text-hud-cyan">
                {cycle?.last_member_collection?.org_sid ?? "—"}
              </div>
            </li>
            <li>
              <div className="text-hud-text-dim">QUEUE STUCK</div>
              <div
                className={
                  (cycle?.queue_stuck ?? 0) > 0
                    ? "mt-1 text-hud-orange"
                    : "mt-1 text-hud-green"
                }
              >
                {formatNumber(cycle?.queue_stuck ?? 0)}
              </div>
            </li>
          </ul>
        </HudPanel>
      </section>
    </div>
  );
}
