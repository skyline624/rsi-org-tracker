import type { ReactNode } from "react";
import { HudPanel } from "./HudPanel";
import { formatCompact } from "@/lib/utils/format";
import { cn } from "@/lib/utils/cn";

interface HudStatTileProps {
  label: string;
  value: number | string;
  sub?: string;
  icon?: ReactNode;
  accent?: "cyan" | "orange" | "green" | "red";
  compact?: boolean;
}

/**
 * Tuile statistique : gros chiffre, label, sous-texte.
 * Utilise `formatCompact` pour les nombres > 1k.
 */
export function HudStatTile({
  label,
  value,
  sub,
  icon,
  accent = "cyan",
  compact = true,
}: HudStatTileProps) {
  const display =
    typeof value === "number" && compact ? formatCompact(value) : value;

  const valueColor = {
    cyan: "text-hud-cyan",
    orange: "text-hud-orange",
    green: "text-hud-green",
    red: "text-hud-red",
  }[accent];

  return (
    <HudPanel accent={accent} className="h-full">
      <div className="hud-label flex items-center gap-2">
        {icon}
        <span>{label}</span>
      </div>
      <div
        className={cn(
          "mt-3 font-display text-4xl leading-none tabular-nums",
          valueColor,
          "[text-shadow:0_0_16px_rgba(0,217,255,0.35)]",
        )}
      >
        {display}
      </div>
      {sub ? (
        <div className="mt-2 font-mono text-[10px] uppercase tracking-wider text-hud-text-dim">
          {sub}
        </div>
      ) : null}
    </HudPanel>
  );
}
