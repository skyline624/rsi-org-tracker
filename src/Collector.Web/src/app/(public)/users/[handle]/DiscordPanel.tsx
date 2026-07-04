import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";

export interface DiscordProfile {
  id: string;
  username: string;
  globalName?: string | null;
  avatarUrl?: string | null;
  badges: string[];
}

/**
 * One or more Discord profiles resolved from the person's manually-added Discord
 * ids (a person may have several accounts). Each id is enriched via our backend.
 */
export function DiscordPanel({ profiles }: { profiles: DiscordProfile[] }) {
  if (profiles.length === 0) return null;

  return (
    <HudPanel label={profiles.length > 1 ? `DISCORD (${profiles.length})` : "DISCORD"}>
      <ul className="flex flex-col gap-4">
        {profiles.map((profile) => (
          <li
            key={profile.id}
            className="flex flex-col gap-2 border-b border-hud-cyan/10 pb-3 last:border-0 last:pb-0"
          >
            <div className="flex items-center gap-3">
              {profile.avatarUrl && (
                // eslint-disable-next-line @next/next/no-img-element -- external Discord CDN, loaded client-side
                <img
                  src={profile.avatarUrl}
                  alt=""
                  width={48}
                  height={48}
                  referrerPolicy="no-referrer"
                  className="rounded-full border border-hud-cyan/30"
                />
              )}
              <div className="flex flex-col">
                <span className="font-mono text-sm text-hud-text">
                  {profile.globalName ?? profile.username}
                </span>
                <span className="font-mono text-[11px] text-hud-text-dim">@{profile.username}</span>
              </div>
            </div>

            {profile.badges.length > 0 && (
              <div className="flex flex-wrap gap-1">
                {profile.badges.map((b) => (
                  <HudBadge key={b} tone="orange">
                    {b}
                  </HudBadge>
                ))}
              </div>
            )}

            <a
              href={`https://discord.com/users/${profile.id}`}
              target="_blank"
              rel="noopener noreferrer"
              className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-cyan"
            >
              ouvrir le profil Discord ↗
            </a>
          </li>
        ))}
      </ul>
    </HudPanel>
  );
}
