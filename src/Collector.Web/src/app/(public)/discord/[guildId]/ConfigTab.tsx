import { HudPanel } from "@/components/hud/HudPanel";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import { GuildOrgForm } from "../GuildOrgForm";
import { RoleConfigTable } from "./RoleConfigTable";

/**
 * The org the server stands for and its rank roles. Anyone signed in may configure an
 * unmapped server; once mapped, only the responsible user and admins (guild.canEdit).
 */
export function ConfigTab({ guild }: { guild: DiscordGuildDetailDto }) {
  const lockedMessage = `Seuls ${guild.orgMappedBy ?? "le responsable"} et les administrateurs peuvent modifier ce serveur.`;
  return (
    <div className="flex flex-col gap-6">
      <HudPanel label="CORPO RELIÉE">
        <p className="mb-3 font-mono text-xs text-hud-text-dim">
          {guild.orgSid
            ? `Relié à ${guild.orgSid}${guild.orgMappedBy ? ` par ${guild.orgMappedBy}` : ""}.`
            : "Ce serveur n'est relié à aucune corpo : tout utilisateur connecté peut le relier."}
        </p>
        <GuildOrgForm guildId={guild.guildId} guildName={guild.name} currentSid={guild.orgSid} currentOrgName={guild.orgName} canEdit={guild.canEdit} />
        {!guild.canEdit && <p className="mt-3 font-mono text-xs text-hud-orange">{lockedMessage}</p>}
      </HudPanel>

      <HudPanel label={`RÔLES · ${guild.roles.length}`}>
        <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
          {"Les rôles cochés sont des rangs. Le rang d'un membre est son rôle-rang d'ordre le plus élevé ; le rang RSI équivalent sert aux recoupements."}
        </p>
        <RoleConfigTable guildId={guild.guildId} roles={guild.roles} rsiRanks={guild.rsiRanks} canEdit={guild.canEdit} />
      </HudPanel>
    </div>
  );
}
