import { HudPanel } from "@/components/hud/HudPanel";
import { HudStatTile } from "@/components/hud/HudStatTile";
import { getDiscordDiscrepancies } from "@/lib/api/endpoints";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { formatShare, formatUtc } from "@/lib/discord/format";
import { DiscrepanciesTable } from "./DiscrepanciesTable";
import { loadOrNotFound } from "./load";
import { UnmappedNotice } from "./UnmappedNotice";

/** Totals on both sides and the gaps with the mapped org's RSI roster (spec § 10.2). */
export async function GapsTab({ ctx, guild }: { ctx: AuthCtx; guild: DiscordGuildDetailDto }) {
  const gaps = await loadOrNotFound(getDiscordDiscrepancies(ctx, guild.guildId));
  if (gaps.orgSid === null || gaps.totals === null) {
    return (
      <HudPanel label="ÉCARTS RSI" accent="orange">
        <UnmappedNotice guildId={guild.guildId} />
      </HudPanel>
    );
  }

  const totals = gaps.totals;
  const readAt = `lu le ${formatUtc(totals.rsiCountsAt)}`;
  return (
    <div className="flex flex-col gap-6">
      <section className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <HudStatTile label="Discord actifs" value={totals.discordActive} sub="hors bots" accent="cyan" />
        <HudStatTile
          label="Liés à RSI"
          value={totals.discordLinked}
          sub={`${formatShare(totals.discordLinked, totals.discordActive)} des actifs`}
          accent="green"
        />
        {totals.rsiBreakdownKnown ? (
          <HudStatTile
            label="RSI visibles"
            value={totals.rsiVisible ?? 0}
            sub={`masqués ${totals.rsiRedacted ?? 0} · cachés ${totals.rsiHidden ?? 0} · ${readAt}`}
            accent="orange"
          />
        ) : (
          <HudStatTile
            label="RSI (total)"
            value={totals.rsiTotalRows ?? "—"}
            sub={`répartition inconnue · ${readAt}`}
            accent="orange"
          />
        )}
        <HudStatTile
          label="Écarts"
          value={gaps.items.length}
          sub={gaps.rsiOnlyAvailable ? "absences comprises" : "absences inconnues"}
          accent="red"
        />
      </section>

      <HudPanel label={`ÉCARTS AVEC ${gaps.orgSid} · ${gaps.items.length}`} accent="orange">
        {!gaps.rsiOnlyAvailable && (
          <p className="mb-3 font-mono text-xs text-hud-orange">Aucun envoi complet : absences inconnues</p>
        )}
        <DiscrepanciesTable rows={gaps.items} />
      </HudPanel>
    </div>
  );
}
