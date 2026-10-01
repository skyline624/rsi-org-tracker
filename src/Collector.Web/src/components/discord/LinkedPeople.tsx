import Link from "next/link";
import { HudBadge } from "@/components/hud/HudBadge";
import type { DiscordLinkedPersonDto } from "@/lib/api/types";

/** The citizens a Discord account is linked to, each a link to its page. */
export function LinkedPeople({ links, multiple = false }: { links: DiscordLinkedPersonDto[]; multiple?: boolean }) {
  if (links.length === 0) return <span className="text-hud-text-dim">—</span>;
  return (
    <span className="flex min-w-0 flex-col items-start gap-0.5">
      {links.map((person, i) =>
        person.handle ? (
          <Link
            key={`${person.handle}-${i}`}
            href={`/users/${encodeURIComponent(person.handle)}`}
            title={person.displayName ?? person.handle}
            className="block max-w-full truncate text-hud-cyan hover:text-hud-orange"
          >
            {person.handle}
          </Link>
        ) : (
          <span key={`citizen-${person.citizenId ?? i}`} className="text-hud-text-dim">
            #{person.citizenId ?? "?"}
          </span>
        ),
      )}
      {multiple && <HudBadge tone="orange">PLUSIEURS LIENS</HudBadge>}
    </span>
  );
}
