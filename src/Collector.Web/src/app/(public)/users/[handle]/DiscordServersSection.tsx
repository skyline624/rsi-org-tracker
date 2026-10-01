import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { RoleChanges } from "@/components/discord/RoleChanges";
import { ValueChange } from "@/components/discord/ValueChange";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudPanel } from "@/components/hud/HudPanel";
import type { DiscordProfileAccountDto, DiscordTimelineEntryDto } from "@/lib/api/types";
import { getSession, sessionCtx } from "@/lib/auth/session";
import {
  discordDisplayName,
  eventTone,
  formatUtc,
  timelineSourceBadge,
  timelineTypeLabel,
  timelineWhen,
} from "@/lib/discord/format";
import { loadUserDiscord } from "@/lib/discord/user-profile";

/** Entries whose values are dates: their type says it all. */
const NO_VALUE_TYPES = new Set(["joined", "left", "rejoined", "member_joined", "member_left"]);

function AccountServers({ account }: { account: DiscordProfileAccountDto }) {
  return (
    <li className="flex flex-col gap-2 border-b border-hud-cyan/10 pb-3 last:border-0 last:pb-0">
      <div className="flex min-w-0 flex-wrap items-baseline gap-2 font-mono text-sm">
        <span className="inline-flex min-w-0 max-w-full text-hud-text">
          <DiscordText value={discordDisplayName(account)} />
        </span>
        <span className="inline-flex min-w-0 max-w-[16rem] text-[11px] text-hud-text-dim">
          @<DiscordText value={account.username} />
        </span>
        <span className="text-[10px] text-hud-text-dim">{account.discordUserId}</span>
      </div>
      {account.guilds.length === 0 ? (
        <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">Aucun serveur suivi.</p>
      ) : (
        <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
          {account.guilds.map((guild) => (
            <li key={guild.guildId} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-1.5">
              <span className="flex min-w-0 flex-1 items-center gap-2">
                <Link
                  href={`/discord/${encodeURIComponent(guild.guildId)}`}
                  className="inline-flex min-w-0 max-w-[20rem] text-hud-cyan hover:text-hud-orange"
                >
                  <DiscordText value={guild.guildName} fallback={guild.guildId} />
                </Link>
                {guild.orgSid && (
                  <Link
                    href={`/orgs/${encodeURIComponent(guild.orgSid)}`}
                    className="shrink-0 text-hud-text-dim hover:text-hud-cyan"
                  >
                    [{guild.orgSid}]
                  </Link>
                )}
                {guild.rank && (
                  <span className="inline-flex min-w-0 max-w-[12rem] text-hud-text">
                    <DiscordText value={guild.rank} />
                  </span>
                )}
              </span>
              <span className="flex items-center gap-2 text-[10px] uppercase tracking-wide text-hud-text-dim">
                <span>arrivée {formatUtc(guild.joinedAt)}</span>
                {guild.leftAt ? (
                  <>
                    <HudBadge tone="red">PARTI</HudBadge>
                    <span>{formatUtc(guild.leftAt)}</span>
                  </>
                ) : (
                  <HudBadge tone="green">PRÉSENT</HudBadge>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}

function TimelinePlace({ entry }: { entry: DiscordTimelineEntryDto }) {
  if (entry.source === "discord" && entry.guildId) {
    return (
      <Link
        href={`/discord/${encodeURIComponent(entry.guildId)}`}
        className="inline-flex min-w-0 max-w-[16rem] text-hud-cyan hover:text-hud-orange"
      >
        <DiscordText value={entry.guildName} fallback={entry.guildId} />
      </Link>
    );
  }
  if (entry.orgSid) {
    return (
      <Link href={`/orgs/${encodeURIComponent(entry.orgSid)}`} className="text-hud-cyan hover:text-hud-orange">
        {entry.orgSid}
      </Link>
    );
  }
  return null;
}

function TimelineDetail({ entry }: { entry: DiscordTimelineEntryDto }) {
  if (entry.type === "roles_changed") {
    // Discord values carry [{id,name}]; RSI ones are raw role labels, unreadable inline.
    return entry.source === "discord" ? <RoleChanges oldValue={entry.oldValue} newValue={entry.newValue} /> : null;
  }
  if (NO_VALUE_TYPES.has(entry.type) || (entry.oldValue === null && entry.newValue === null)) return null;
  return <ValueChange oldValue={entry.oldValue} newValue={entry.newValue} />;
}

/**
 * « DISCORD · SERVEURS » (spec § 10.4): the Discord accounts linked to the citizen, the
 * servers each is or was on, and one timeline of RSI and Discord events. Distinct from
 * DiscordPanel, which shows the public profile of a manually added Discord id.
 */
export async function DiscordServersSection({ handle }: { handle: string }) {
  const session = await getSession();
  if (!session) return null;
  const state = await loadUserDiscord(sessionCtx(session), handle);

  return (
    <HudPanel label="DISCORD · SERVEURS">
      {state.kind === "unavailable" ? (
        <p className="py-4 text-center font-mono text-xs text-hud-red">Données Discord indisponibles.</p>
      ) : state.kind === "empty" ? (
        <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
          {"Aucun compte Discord lié à ce citoyen. Les liens se valident dans l'onglet Suggestions d'un serveur Discord."}
        </p>
      ) : (
        <div className="flex flex-col gap-6">
          <ul className="flex flex-col gap-4">
            {state.profile.accounts.map((account) => (
              <AccountServers key={account.discordUserId} account={account} />
            ))}
          </ul>
          {state.profile.timeline.length > 0 && (
            <div className="flex flex-col gap-2">
              <div className="hud-label text-hud-text-dim">FRISE RSI + DISCORD</div>
              <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
                {state.profile.timeline.map((entry, i) => {
                  const source = timelineSourceBadge(entry.source);
                  return (
                    <li
                      key={`${entry.source}-${entry.type}-${entry.at}-${i}`}
                      className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2"
                    >
                      <span className="flex min-w-0 flex-1 flex-wrap items-center gap-2">
                        <HudBadge tone={source.tone}>{source.label}</HudBadge>
                        <HudBadge tone={eventTone(entry.type)}>{timelineTypeLabel(entry)}</HudBadge>
                        <TimelinePlace entry={entry} />
                        <TimelineDetail entry={entry} />
                      </span>
                      <time className="text-[10px] text-hud-text-dim">{timelineWhen(entry)}</time>
                    </li>
                  );
                })}
              </ul>
            </div>
          )}
        </div>
      )}
    </HudPanel>
  );
}
