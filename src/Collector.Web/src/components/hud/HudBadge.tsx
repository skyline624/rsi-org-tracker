import type { ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

type Tone = "cyan" | "orange" | "red" | "green" | "dim";

interface HudBadgeProps {
  tone?: Tone;
  children: ReactNode;
  className?: string;
}

const tones: Record<Tone, string> = {
  cyan: "border-hud-cyan/50 text-hud-cyan bg-hud-cyan/10",
  orange: "border-hud-orange/60 text-hud-orange bg-hud-orange/10",
  red: "border-hud-red/60 text-hud-red bg-hud-red/10",
  green: "border-hud-green/50 text-hud-green bg-hud-green/10",
  dim: "border-hud-text-dim/40 text-hud-text-dim bg-hud-text-dim/5",
};

/** Pill HUD type status badge ("ACTIVE", "RECRUITING", "BANNED"…). */
export function HudBadge({ tone = "cyan", children, className }: HudBadgeProps) {
  return (
    <span
      className={cn(
        "inline-flex items-center border px-2 py-0.5 font-mono text-[10px] uppercase tracking-[0.15em]",
        tones[tone],
        className,
      )}
    >
      {children}
    </span>
  );
}
