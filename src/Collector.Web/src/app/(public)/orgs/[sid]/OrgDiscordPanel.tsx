import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudPanel } from "@/components/hud/HudPanel";
import type { DiscordOrgGuildDto } from "@/lib/api/types";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { formatNumber } from "@/lib/utils/format";

/** Discord servers mapped to this org (principal, recruitment…); hidden when there is none. */
export function OrgDiscordPanel({ guilds }: { guilds: DiscordOrgGuildDto[] }) {
  if (guilds.length === 0) return null;

  return (
    <HudPanel label={guilds.length > 1 ? `DISCORD · ${guilds.length} SERVEURS` : "DISCORD"}>
      <ul className="flex flex-col divide-y divide-hud-cyan/10">
        {guilds.map((guild) => (
          <li
            key={guild.guildId}
            className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2 font-mono text-sm"
          >
            <Link
              href={`/discord/${encodeURIComponent(guild.guildId)}`}
              className="flex min-w-0 flex-1 items-center gap-3 text-hud-cyan hover:text-hud-orange"
            >
              <GuildIcon guildId={guild.guildId} iconHash={guild.iconHash} />
              <DiscordText value={guild.name} fallback={guild.guildId} className="min-w-0" />
            </Link>
            <span className="flex flex-wrap items-center gap-2 text-[10px] uppercase tracking-wide text-hud-text-dim">
              <span>
                {formatNumber(guild.activeMembers)} actifs · {formatNumber(guild.linkedMembers)} liés
              </span>
              {syncBadges({ isComplete: guild.lastSyncComplete, massDepartureDetected: false }).map((b) => (
                <HudBadge key={b.label} tone={b.tone}>
                  {b.label}
                </HudBadge>
              ))}
              <span>{formatUtc(guild.lastSyncAt)}</span>
            </span>
          </li>
        ))}
      </ul>
    </HudPanel>
  );
}
