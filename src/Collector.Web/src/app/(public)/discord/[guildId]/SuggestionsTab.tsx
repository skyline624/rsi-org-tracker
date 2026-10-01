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
          ? "Sans corpo reliée, seules les correspondances de confiance moyenne sont proposées."
          : "Confiance forte : le handle est membre actif de la corpo reliée. Seuls les liens validés comptent."}
      </p>
      <SuggestionsList suggestions={suggestions} />
    </HudPanel>
  );
}
