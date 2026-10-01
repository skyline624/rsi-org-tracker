import Link from "next/link";
import { notFound } from "next/navigation";
import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { HudBadge } from "@/components/hud/HudBadge";
import { getDiscordGuild } from "@/lib/api/endpoints";
import { requireAuthCtx } from "@/lib/auth/server-api";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { parseDiscordTab, type SearchParams } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { snowflakeSchema, valid } from "@/lib/validation";
import { ConfigTab } from "./ConfigTab";
import { GapsTab } from "./GapsTab";
import { GuildTabNav } from "./GuildTabNav";
import { HistoryTab } from "./HistoryTab";
import { loadOrNotFound } from "./load";
import { MembersTab } from "./MembersTab";
import { SuggestionsTab } from "./SuggestionsTab";
import { SyncsTab } from "./SyncsTab";

export const dynamic = "force-dynamic";

interface PageProps {
  params: Promise<{ guildId: string }>;
  searchParams: Promise<SearchParams>;
}

/**
 * A tracked Discord server: header with its org and last upload, and one
 * tab at a time (?tab=), each loading only its own data.
 */
export default async function DiscordGuildPage({ params, searchParams }: PageProps) {
  const [{ guildId }, sp] = await Promise.all([params, searchParams]);
  // Not a snowflake: no such server, and nothing forged reaches the API.
  if (!valid(snowflakeSchema, guildId)) notFound();

  const ctx = await requireAuthCtx();
  const guild = await loadOrNotFound(getDiscordGuild(ctx, guildId));
  const tab = parseDiscordTab(sp.tab);

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-start justify-between gap-4 border-b border-hud-cyan/30 pb-4">
        <div className="flex min-w-0 flex-1 items-center gap-4">
          <GuildIcon guildId={guild.guildId} iconHash={guild.iconHash} size={56} />
          <div className="min-w-0 flex-1">
            <div className="hud-label">— UEE::DISCORD_SERVER</div>
            <h1 className="mt-1 flex min-w-0 font-display text-3xl">
              <DiscordText value={guild.name} fallback={guild.guildId} />
            </h1>
            <div className="mt-2 flex flex-wrap items-center gap-2 font-mono text-xs text-hud-text-dim">
              {guild.orgSid ? (
                <Link href={`/orgs/${encodeURIComponent(guild.orgSid)}`} className="text-hud-cyan hover:text-hud-orange">
                  [{guild.orgSid}] {guild.orgName ?? ""}
                </Link>
              ) : (
                <HudBadge tone="orange">NON RELIÉ</HudBadge>
              )}
              {guild.orgMappedBy && <span>· relié par {guild.orgMappedBy}</span>}
              <span>· {formatNumber(guild.activeMembers)} actifs</span>
              {guild.lastSync && (
                <>
                  <span>· dernier envoi {formatUtc(guild.lastSync.receivedAt)}</span>
                  {syncBadges(guild.lastSync).map((b) => (
                    <HudBadge key={b.label} tone={b.tone}>
                      {b.label}
                    </HudBadge>
                  ))}
                </>
              )}
            </div>
          </div>
        </div>
      </header>

      <GuildTabNav guildId={guild.guildId} active={tab} />

      {tab === "members" && <MembersTab ctx={ctx} guild={guild} searchParams={sp} />}
      {tab === "history" && <HistoryTab ctx={ctx} guildId={guild.guildId} searchParams={sp} />}
      {tab === "gaps" && <GapsTab ctx={ctx} guild={guild} />}
      {tab === "suggestions" && <SuggestionsTab ctx={ctx} guild={guild} />}
      {tab === "config" && <ConfigTab guild={guild} />}
      {tab === "syncs" && <SyncsTab ctx={ctx} guildId={guild.guildId} />}
    </div>
  );
}
