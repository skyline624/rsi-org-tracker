import { DiscordText } from "@/components/discord/DiscordText";
import { GuildIcon } from "@/components/discord/GuildIcon";
import { HudPanel } from "@/components/hud/HudPanel";
import { listDiscordGuilds } from "@/lib/api/endpoints";
import { requireAuthCtx, withAuthRedirect } from "@/lib/auth/server-api";
import { formatNumber } from "@/lib/utils/format";
import { DiscordGuildsTable } from "./DiscordGuildsTable";
import { GuildOrgForm } from "./GuildOrgForm";

export const dynamic = "force-dynamic";

/** Servers uploaded by the Vencord plugin; the ones not mapped to an org yet come first, with their SID form. */
export default async function DiscordPage() {
  const ctx = await requireAuthCtx();
  const guilds = await withAuthRedirect(listDiscordGuilds(ctx));
  const unmapped = guilds.filter((g) => g.orgSid === null);

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <div className="hud-label">— UEE::DISCORD_ROSTERS</div>
          <h1 className="mt-1 font-display text-3xl">Serveurs Discord</h1>
          <p className="mt-1 font-mono text-xs text-hud-text-dim">
            Membres envoyés par le plugin Vencord, serveur par serveur, à la demande.
          </p>
        </div>
        <a
          href="/discord/multi"
          className="hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10"
        >
          MULTI-APPARTENANCE
        </a>
      </header>

      {guilds.length === 0 ? (
        <HudPanel label="SERVEURS DISCORD">
          <p className="py-6 text-center font-mono text-xs text-hud-text-dim">
            Aucun serveur reçu. Installe le plugin :{" "}
            <a href="/settings" className="text-hud-cyan hover:text-hud-orange">
              {"Paramètres → Clé d'envoi Discord"}
            </a>
          </p>
        </HudPanel>
      ) : (
        <>
          {unmapped.length > 0 && (
            <HudPanel label={`SERVEURS NON RELIÉS · ${unmapped.length}`} accent="orange">
              <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
                {"Relie chaque serveur à la corpo RSI qu'il représente : ses rôles deviennent des rangs et ses membres sont recoupés avec le roster RSI."}
              </p>
              <ul className="flex flex-col divide-y divide-hud-cyan/10">
                {unmapped.map((g) => (
                  <li key={g.guildId} className="flex flex-wrap items-center justify-between gap-3 py-3">
                    <a
                      href={`/discord/${encodeURIComponent(g.guildId)}`}
                      className="flex min-w-0 flex-1 items-center gap-3 font-mono text-sm text-hud-cyan hover:text-hud-orange"
                    >
                      <GuildIcon guildId={g.guildId} iconHash={g.iconHash} />
                      <DiscordText value={g.name} className="min-w-0" />
                      <span className="shrink-0 text-[10px] text-hud-text-dim">
                        {formatNumber(g.activeMembers)} actifs
                      </span>
                    </a>
                    <GuildOrgForm guildId={g.guildId} guildName={g.name} currentSid={null} canEdit />
                  </li>
                ))}
              </ul>
            </HudPanel>
          )}

          <HudPanel label={`${formatNumber(guilds.length)} SERVEURS SUIVIS`}>
            <DiscordGuildsTable rows={guilds} />
          </HudPanel>
        </>
      )}
    </div>
  );
}
