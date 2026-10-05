import { HudPanel } from "@/components/hud/HudPanel";
import { getDiscordSuggestions } from "@/lib/api/endpoints";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { loadOrNotFound } from "./load";
import { SuggestionsList } from "./SuggestionsList";
import { UnmappedNotice } from "./UnmappedNotice";

/** Proposed Discord ↔ RSI links for the unlinked members; only validated links count. */
export async function SuggestionsTab({ ctx, guild }: { ctx: AuthCtx; guild: DiscordGuildDetailDto }) {
  const suggestions = await loadOrNotFound(getDiscordSuggestions(ctx, guild.guildId));

  return (
    <HudPanel label={`SUGGESTIONS DE LIENS · ${suggestions.length}`}>
      {guild.orgSid === null && (
        <div className="mb-3">
          <UnmappedNotice guildId={guild.guildId} />
        </div>
      )}
      <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
        {guild.orgSid === null
          ? "Sans corpo reliée, seul le tag de corpo d'un membre rend une correspondance forte : son handle est membre actif de la corpo de son tag. Elle est rattachée automatiquement ; les autres restent à valider."
          : "Les correspondances de confiance forte sont rattachées automatiquement : le handle est membre actif de la corpo reliée, ou de la corpo du tag du membre. Les autres restent à valider."}
      </p>
      <SuggestionsList suggestions={suggestions} />
    </HudPanel>
  );
}
