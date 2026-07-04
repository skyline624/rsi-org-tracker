import Link from "next/link";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";

export interface OrgManualMember {
  handle: string;
  displayName?: string | null;
  rank?: string | null;
  via: string;
  sinceDate: string;
}

/** Members attached to this org by hand (kept separate from the collected RSI roster). */
export function ManualMembersPanel({ members }: { members: OrgManualMember[] }) {
  if (members.length === 0) return null;

  return (
    <HudPanel label={`MEMBRES AJOUTÉS MANUELLEMENT · ${members.length}`} accent="orange">
      <ul className="flex flex-col divide-y divide-hud-cyan/10">
        {members.map((m) => (
          <li
            key={m.handle}
            className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-2 font-mono text-sm"
          >
            <Link
              href={`/users/${encodeURIComponent(m.handle)}`}
              className="text-hud-cyan hover:underline"
            >
              {m.handle}
              {m.displayName && m.displayName !== m.handle && (
                <span className="ml-2 text-hud-text-dim">{m.displayName}</span>
              )}
            </Link>
            <span className="flex items-center gap-2 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
              {m.rank && <span>{m.rank}</span>}
              <HudBadge tone={m.via === "rsi" ? "green" : "dim"}>{m.via}</HudBadge>
            </span>
          </li>
        ))}
      </ul>
    </HudPanel>
  );
}
