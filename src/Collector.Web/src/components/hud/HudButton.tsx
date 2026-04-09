"use client";
import { forwardRef, type ButtonHTMLAttributes } from "react";
import { cn } from "@/lib/utils/cn";

type Variant = "primary" | "ghost" | "danger" | "orange";

interface HudButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
}

const variants: Record<Variant, string> = {
  primary:
    "border-hud-cyan text-hud-cyan hover:bg-hud-cyan/10 hover:shadow-hud-glow active:bg-hud-cyan/20",
  ghost:
    "border-hud-text-dim/50 text-hud-text-dim hover:border-hud-cyan hover:text-hud-cyan",
  danger:
    "border-hud-red text-hud-red hover:bg-hud-red/10 hover:shadow-hud-glow-red",
  orange:
    "border-hud-orange text-hud-orange hover:bg-hud-orange/10 hover:shadow-hud-glow-orange",
};

/**
 * Bouton HUD : bordure 1 px, coin coupé, glow cyan au hover.
 * Passe `variant` pour changer la couleur.
 */
export const HudButton = forwardRef<HTMLButtonElement, HudButtonProps>(
  function HudButton(
    { variant = "primary", className, children, disabled, ...rest },
    ref,
  ) {
    return (
      <button
        ref={ref}
        disabled={disabled}
        {...rest}
        className={cn(
          "hud-clip relative inline-flex items-center gap-2 px-4 py-2",
          "border font-mono text-xs uppercase tracking-[0.2em] transition-all duration-150",
          "disabled:cursor-not-allowed disabled:opacity-40",
          variants[variant],
          className,
        )}
      >
        {children}
      </button>
    );
  },
);
