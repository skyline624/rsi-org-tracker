import { HudBadge } from "@/components/hud/HudBadge";
import { HudPanel } from "@/components/hud/HudPanel";
import { UserOrgsTable } from "@/components/user/UserOrgsTable";
import type { OrganizationMemberDto } from "@/lib/api/types";

interface PartialUserProfileProps {
  handle: string;
  orgs: OrganizationMemberDto[];
}

export function PartialUserProfile({ handle, orgs }: PartialUserProfileProps) {
  const displayName =
    orgs.find((o) => o.displayName)?.displayName ?? handle;

  return (
    <div className="flex flex-col gap-8">
      <HudPanel label="ENRICHMENT_PENDING" accent="orange">
        <div className="flex flex-col gap-2 font-mono text-xs text-hud-text">
          <p>
            — Profil connu comme membre d&apos;organisation mais pas encore
            enrichi. Les détails (bio, localisation, date d&apos;enrôlement,
            avatar) seront disponibles après le prochain cycle de collecte.
          </p>
          <p className="text-hud-text-dim">
            Réessaie dans quelques minutes.
          </p>
        </div>
      </HudPanel>

      <section className="border-b border-hud-cyan/30 pb-6">
        <div className="hud-label">— UEE::CITIZEN_RECORD (PARTIAL)</div>
        <h1 className="mt-1 font-display text-4xl font-bold">{displayName}</h1>
        <div className="mt-2 flex flex-wrap items-center gap-2 font-mono text-xs text-hud-text-dim">
          <span className="text-hud-cyan">@{handle}</span>
          <HudBadge tone="orange">awaiting enrichment</HudBadge>
        </div>
      </section>

      <section>
        <HudPanel label="ORG MEMBERSHIPS">
          <UserOrgsTable rows={orgs} />
        </HudPanel>
      </section>
    </div>
  );
}
