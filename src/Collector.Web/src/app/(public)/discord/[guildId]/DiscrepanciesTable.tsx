"use client";
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordDiscrepancyDto } from "@/lib/api/types";
import { discrepancyKindBadge } from "@/lib/discord/format";

/** Gaps between the server and its org's RSI roster; paged in the browser (rsi_only can be long). */
export function DiscrepanciesTable({ rows }: { rows: DiscordDiscrepancyDto[] }) {
  const columns: HudColumn<DiscordDiscrepancyDto>[] = [
    {
      key: "kind",
      header: "ÉCART",
      width: "w-60",
      sortable: true,
      sortValue: (d) => d.kind,
      render: (d) => {
        const badge = discrepancyKindBadge(d.kind);
        return <HudBadge tone={badge.tone}>{badge.label}</HudBadge>;
      },
    },
    {
      key: "citizen",
      header: "CITOYEN RSI",
      width: "w-44 min-w-0",
      sortable: true,
      sortValue: (d) => d.handle?.toLowerCase() ?? null,
      render: (d) =>
        d.handle ? (
          <Link
            href={`/users/${encodeURIComponent(d.handle)}`}
            className="block truncate text-hud-cyan hover:text-hud-orange"
          >
            {d.handle}
          </Link>
        ) : (
          <span className="text-hud-text-dim">{d.citizenId !== null ? `#${d.citizenId}` : "—"}</span>
        ),
    },
    {
      key: "discord",
      header: "COMPTE DISCORD",
      width: "min-w-0 flex-1",
      render: (d) => <DiscordText value={d.discordName} fallback={d.discordUserId ?? "—"} />,
    },
    {
      key: "discordRank",
      header: "RANG DISCORD",
      width: "w-40 min-w-0",
      render: (d) => <DiscordText value={d.discordRank} />,
    },
    {
      key: "rsiRank",
      header: "RANG RSI",
      width: "w-40 min-w-0",
      render: (d) => <DiscordText value={d.rsiRank} />,
    },
  ];

  return (
    <HudDataGrid
      minWidth={944}
      columns={columns}
      rows={rows}
      rowKey={(d, i) => `${d.kind}:${d.discordUserId ?? ""}:${d.handle ?? ""}:${i}`}
      empty="Aucun écart."
      paginated
      pageSizeOptions={[25, 50, 100, 0]}
    />
  );
}
