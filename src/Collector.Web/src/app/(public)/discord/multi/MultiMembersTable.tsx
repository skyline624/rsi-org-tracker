"use client";
import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { LinkedPeople } from "@/components/discord/LinkedPeople";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordMultiMemberDto } from "@/lib/api/types";
import { discordDisplayName } from "@/lib/discord/format";

/** One API page of accounts present on at least two tracked servers (spec § 10.3). */
export function MultiMembersTable({ rows }: { rows: DiscordMultiMemberDto[] }) {
  const columns: HudColumn<DiscordMultiMemberDto>[] = [
    {
      key: "account",
      header: "COMPTE",
      width: "w-56 min-w-0",
      render: (m) => (
        <span className="flex min-w-0 flex-col">
          <span className="inline-flex min-w-0 max-w-full text-hud-text">
            <DiscordText value={discordDisplayName(m)} />
          </span>
          <span className="inline-flex min-w-0 max-w-full text-[10px] text-hud-text-dim">
            @<DiscordText value={m.username} />
          </span>
        </span>
      ),
    },
    {
      key: "guilds",
      header: "SERVEURS",
      width: "min-w-0 flex-1",
      render: (m) => (
        <ul className="flex min-w-0 flex-col gap-0.5">
          {m.guilds.map((guild) => (
            <li key={guild.guildId} className="flex min-w-0 items-center gap-2">
              <Link
                href={`/discord/${encodeURIComponent(guild.guildId)}`}
                className="inline-flex min-w-0 max-w-[16rem] text-hud-cyan hover:text-hud-orange"
              >
                <DiscordText value={guild.guildName} fallback={guild.guildId} />
              </Link>
              {guild.orgSid ? (
                <Link
                  href={`/orgs/${encodeURIComponent(guild.orgSid)}`}
                  className="shrink-0 text-hud-text-dim hover:text-hud-cyan"
                >
                  [{guild.orgSid}]
                </Link>
              ) : (
                <span className="shrink-0 text-[10px] text-hud-text-dim">non relié</span>
              )}
              {guild.rank && (
                <span className="inline-flex min-w-0 max-w-[10rem] text-hud-text">
                  <DiscordText value={guild.rank} />
                </span>
              )}
            </li>
          ))}
        </ul>
      ),
    },
    {
      key: "links",
      header: "CITOYENS LIÉS",
      width: "w-40 min-w-0",
      render: (m) => <LinkedPeople links={m.links} />,
    },
    {
      key: "rsiOrgs",
      header: "ORGS RSI",
      width: "w-48 min-w-0",
      render: (m) =>
        m.rsiOrgs.length === 0 ? (
          <span className="text-hud-text-dim">—</span>
        ) : (
          <span className="flex min-w-0 flex-col gap-0.5">
            {m.rsiOrgs.map((org) => (
              <span key={org.sid} className="flex min-w-0 items-center gap-2">
                <Link
                  href={`/orgs/${encodeURIComponent(org.sid)}`}
                  className="shrink-0 text-hud-cyan hover:text-hud-orange"
                >
                  {org.sid}
                </Link>
                {org.rank && <span className="truncate text-hud-text-dim">{org.rank}</span>}
              </span>
            ))}
          </span>
        ),
    },
  ];

  return (
    <HudDataGrid
      minWidth={864}
      columns={columns}
      rows={rows}
      rowKey={(m) => m.discordUserId}
      empty="Aucun compte n'est présent sur au moins deux serveurs suivis."
    />
  );
}
