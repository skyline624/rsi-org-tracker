import { guildIconUrl } from "@/lib/discord/format";
import { cn } from "@/lib/utils/cn";

interface GuildIconProps {
  guildId: string;
  iconHash: string | null;
  /** Width and height in pixels. */
  size?: number;
  className?: string;
}

/** A server icon from Discord's CDN, or an empty square when the server has none (or a bad hash). */
export function GuildIcon({ guildId, iconHash, size = 32, className }: GuildIconProps) {
  const src = guildIconUrl(guildId, iconHash);
  if (!src) {
    return (
      <span
        aria-hidden
        className={cn("inline-block shrink-0 rounded-full border border-hud-cyan/30 bg-hud-bg/60", className)}
        style={{ width: size, height: size }}
      />
    );
  }
  return (
    // eslint-disable-next-line @next/next/no-img-element -- Discord CDN, no /_next/image optimizer (next.config.ts)
    <img
      src={src}
      alt=""
      width={size}
      height={size}
      loading="lazy"
      referrerPolicy="no-referrer"
      className={cn("shrink-0 rounded-full border border-hud-cyan/30", className)}
    />
  );
}
