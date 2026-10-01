"use client";
import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { RoleDot } from "@/components/discord/RoleDot";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordGuildSummaryDto } from "@/lib/api/types";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { formatNumber } from "@/lib/utils/format";

/** Ranks listed per server; the others are counted. */
const TOP_RANKS = 3;

function RankSummary({ ranks }: { ranks: DiscordGuildSummaryDto["rankDistribution"] }) {
  if (ranks.length === 0) return <span className="text-hud-text-dim">—</span>;
  return (
    <span className="flex min-w-0 flex-col gap-0.5">
      {ranks.slice(0, TOP_RANKS).map((r) => (
        <span key={r.roleId} className="flex min-w-0 items-center gap-1">
          <RoleDot color={r.color} />
          <DiscordText value={r.name} className="min-w-0" />
          <span className="shrink-0 tabular-nums text-hud-text-dim">×{formatNumber(r.count)}</span>
        </span>
      ))}
      {ranks.length > TOP_RANKS && (
        <span className="text-[10px] text-hud-text-dim">+{ranks.length - TOP_RANKS} autres rangs</span>
      )}
    </span>
  );
}

function LastSync({ sync }: { sync: DiscordGuildSummaryDto["lastSync"] }) {
  if (!sync) return <span className="text-hud-text-dim">—</span>;
  return (
    <span className="flex flex-col gap-1">
      <span className="flex flex-wrap gap-1">
        {syncBadges(sync).map((b) => (
          <HudBadge key={b.label} tone={b.tone}>
            {b.label}
          </HudBadge>
        ))}
      </span>
      <span className="text-[10px] text-hud-text-dim">
        {formatUtc(sync.receivedAt)} · {sync.submittedBy}
      </span>
    </span>
  );
}

/** Tracked servers: org, active members, main ranks and last upload. Unmapped ones come first (API order). */
export function DiscordGuildsTable({ rows }: { rows: DiscordGuildSummaryDto[] }) {
  const columns: HudColumn<DiscordGuildSummaryDto>[] = [
    {
      key: "icon",
      header: "",
      width: "w-14",
      render: (g) => <GuildIcon guildId={g.guildId} iconHash={g.iconHash} />,
    },
    {
      key: "name",
      header: "SERVEUR",
      width: "min-w-0 flex-1",
      sortable: true,
      sortValue: (g) => g.name.toLowerCase(),
      render: (g) => (
        <a
          href={`/discord/${encodeURIComponent(g.guildId)}`}
          className="inline-flex min-w-0 max-w-full text-hud-cyan hover:text-hud-orange"
        >
          <DiscordText value={g.name} />
        </a>
      ),
    },
    {
      key: "org",
      header: "CORPO",
      width: "w-36 min-w-0",
      sortable: true,
      sortValue: (g) => g.orgSid ?? "",
      render: (g) =>
        g.orgSid ? (
          <a
            href={`/orgs/${encodeURIComponent(g.orgSid)}`}
            title={g.orgName ?? g.orgSid}
            className="block truncate text-hud-cyan hover:text-hud-orange"
          >
            {g.orgSid}
          </a>
        ) : (
          <HudBadge tone="orange">NON RELIÉ</HudBadge>
        ),
    },
    {
      key: "active",
      header: "ACTIFS",
      width: "w-20",
      align: "right",
      sortable: true,
      sortValue: (g) => g.activeMembers,
      render: (g) => formatNumber(g.activeMembers),
    },
    {
      key: "ranks",
      header: "RANGS",
      width: "w-56 min-w-0",
      render: (g) => <RankSummary ranks={g.rankDistribution} />,
    },
    {
      key: "lastSync",
      header: "DERNIER ENVOI",
      width: "w-60",
      sortable: true,
      sortValue: (g) => g.lastSync?.receivedAt ?? null,
      render: (g) => <LastSync sync={g.lastSync} />,
    },
  ];

  return <HudDataGrid minWidth={960} columns={columns} rows={rows} rowKey={(g) => g.guildId} empty="Aucun serveur." />;
}
