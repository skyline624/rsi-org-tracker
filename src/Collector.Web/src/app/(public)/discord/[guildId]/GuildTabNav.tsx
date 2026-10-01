import { DISCORD_TABS, TAB_LABELS, tabHref, type DiscordTab } from "@/lib/discord/params";
import { cn } from "@/lib/utils/cn";

/** Each tab loads its server-rendered page, avoiding stalled client transitions between query variants. */
export function GuildTabNav({ guildId, active }: { guildId: string; active: DiscordTab }) {
  return (
    <nav aria-label="Onglets du serveur" className="flex flex-wrap gap-1 border-b border-hud-cyan/20 font-mono text-xs">
      {DISCORD_TABS.map((tab) => (
        <a
          key={tab}
          href={tabHref(guildId, tab)}
          aria-current={tab === active ? "page" : undefined}
          className={cn(
            "relative px-3 py-2 uppercase tracking-[0.2em] transition-colors",
            tab === active ? "text-hud-cyan" : "text-hud-text-dim hover:text-hud-text",
          )}
        >
          {TAB_LABELS[tab]}
          {tab === active && (
            <span className="absolute inset-x-1 -bottom-px h-px bg-hud-cyan shadow-[0_0_8px_var(--hud-cyan)]" />
          )}
        </a>
      ))}
    </nav>
  );
}
