"use client";
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { LinkedPeople } from "@/components/discord/LinkedPeople";
import { RoleDot } from "@/components/discord/RoleDot";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordMemberDto } from "@/lib/api/types";
import { discordDisplayName, formatUtc, reconciliationBadge } from "@/lib/discord/format";
import { tabHref } from "@/lib/discord/params";

interface DiscordMembersTableProps {
  rows: DiscordMemberDto[];
  guildId: string;
}

/** One API page of members: name, nick, rank, linked citizens, RSI rank and reconciliation status. */
export function DiscordMembersTable({ rows, guildId }: DiscordMembersTableProps) {

  const columns: HudColumn<DiscordMemberDto>[] = [
    {
      key: "member",
      header: "MEMBRE",
      width: "min-w-0 flex-1",
      render: (m) => (
        <span className="flex min-w-0 flex-col">
          <span className="flex min-w-0 items-center gap-2">
            <Link
              href={tabHref(guildId, "history", { userId: m.discordUserId })}
              title="Historique de ce membre"
              className="inline-flex min-w-0 max-w-full text-hud-text hover:text-hud-cyan"
            >
              <DiscordText value={discordDisplayName(m)} />
            </Link>
            {m.isBot && <HudBadge tone="dim">BOT</HudBadge>}
          </span>
          <span className="inline-flex min-w-0 max-w-full text-[10px] text-hud-text-dim">
            @<DiscordText value={m.username} />
          </span>
        </span>
      ),
    },
    {
      key: "nick",
      header: "PSEUDO",
      width: "w-36 min-w-0",
      render: (m) => <DiscordText value={m.nick} />,
    },
    {
      key: "rank",
      header: "RANG",
      width: "w-40 min-w-0",
      render: (m) =>
        m.rank ? (
          <span className="inline-flex min-w-0 max-w-full items-center gap-1 border border-hud-cyan/30 px-1.5 py-0.5">
            <RoleDot color={m.rank.color} />
            <DiscordText value={m.rank.name} />
          </span>
        ) : (
          <span className="text-hud-text-dim">—</span>
        ),
    },
    {
      key: "links",
      header: "CITOYENS LIÉS",
      width: "w-40 min-w-0",
      render: (m) => <LinkedPeople links={m.links} multiple={m.multipleLinks} />,
    },
    {
      key: "rsiRank",
      header: "RANG RSI",
      width: "w-32 min-w-0",
      render: (m) => <DiscordText value={m.rsiRank} />,
    },
    {
      key: "reconciliation",
      header: "RECOUPEMENT",
      width: "w-44",
      render: (m) => {
        const badge = reconciliationBadge(m.reconciliation);
        return badge ? <HudBadge tone={badge.tone}>{badge.label}</HudBadge> : <span className="text-hud-text-dim">—</span>;
      },
    },
    {
      key: "dates",
      header: "ARRIVÉE",
      width: "w-36",
      render: (m) => (
        <span className="flex flex-col text-[10px] text-hud-text-dim">
          <span>{formatUtc(m.joinedAt)}</span>
          {m.leftAt && <span className="text-hud-red">parti {formatUtc(m.leftAt)}</span>}
        </span>
      ),
    },
  ];

  return (
    <HudDataGrid
      minWidth={1216}
      columns={columns}
      rows={rows}
      rowKey={(m) => m.discordUserId}
      empty="Aucun membre ne correspond à ces filtres."
    />
  );
}
