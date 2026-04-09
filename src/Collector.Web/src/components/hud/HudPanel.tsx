import type { HTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

interface HudPanelProps extends HTMLAttributes<HTMLDivElement> {
  label?: string;
  accent?: "cyan" | "orange" | "red" | "green";
  children: ReactNode;
}

const accentRing = {
  cyan: "ring-hud-cyan/40 shadow-[0_0_24px_-8px_var(--hud-cyan)]",
  orange: "ring-hud-orange/50 shadow-[0_0_24px_-8px_var(--hud-orange)]",
  red: "ring-hud-red/50 shadow-[0_0_24px_-8px_var(--hud-red)]",
  green: "ring-hud-green/40 shadow-[0_0_24px_-8px_var(--hud-green)]",
} as const;

const accentLabel = {
  cyan: "text-hud-cyan",
  orange: "text-hud-orange",
  red: "text-hud-red",
  green: "text-hud-green",
} as const;

/**
 * Encadré HUD réutilisable : coin coupé en biseau, bordure néon 1 px, label mono optionnel.
 * Utiliser partout où on veut matérialiser un "bloc" de l'interface cockpit.
 */
export function HudPanel({
  label,
  accent = "cyan",
  children,
  className,
  ...rest
}: HudPanelProps) {
  return (
    <div
      {...rest}
      className={cn(
        "hud-clip relative bg-hud-elevated ring-1",
        accentRing[accent],
        "p-4",
        className,
      )}
    >
      {label ? (
        <div className={cn("hud-label mb-3", accentLabel[accent])}>
          — {label}
        </div>
      ) : null}
      {children}
      {/* Tick corner en bas-droite, détail visuel HUD */}
      <div
        aria-hidden
        className="pointer-events-none absolute bottom-0 right-0 h-2 w-2 border-b border-r border-hud-cyan/60"
      />
    </div>
  );
}
