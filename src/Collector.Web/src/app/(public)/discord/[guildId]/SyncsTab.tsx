import { HudPanel } from "@/components/hud/HudPanel";
import { getDiscordSyncs } from "@/lib/api/endpoints";
import type { AuthCtx } from "@/lib/auth/server-api";
import { SYNCS_LIMIT } from "@/lib/discord/params";
import { loadOrNotFound } from "./load";
import { SyncsTable } from "./SyncsTable";

/** The last uploads of the server, newest first. */
export async function SyncsTab({ ctx, guildId }: { ctx: AuthCtx; guildId: string }) {
  const syncs = await loadOrNotFound(getDiscordSyncs(ctx, guildId, SYNCS_LIMIT));
  return (
    <HudPanel label={`ENVOIS · ${syncs.length} DERNIERS`}>
      <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
        Un envoi partiel met à jour les membres reçus sans déclarer de départs.
        La collecte par rôles ou cache peut omettre des membres ; une recherche exhaustive est nécessaire pour un envoi complet.
        Les départs massifs d’un envoi complet sont enregistrés et signalés par le tracker.
      </p>
      <SyncsTable rows={syncs} />
    </HudPanel>
  );
}
