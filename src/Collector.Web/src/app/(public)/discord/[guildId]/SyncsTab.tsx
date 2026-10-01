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
        Garde-fou : un envoi complet qui ferait partir plus de 25 % des membres est traité comme partiel.
      </p>
      <SyncsTable rows={syncs} />
    </HudPanel>
  );
}
