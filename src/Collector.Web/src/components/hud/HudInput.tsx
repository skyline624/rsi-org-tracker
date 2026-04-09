"use client";
import { forwardRef, type InputHTMLAttributes } from "react";
import { cn } from "@/lib/utils/cn";

interface HudInputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string;
}

export const HudInput = forwardRef<HTMLInputElement, HudInputProps>(
  function HudInput({ label, className, id, ...rest }, ref) {
    return (
      <label className="flex flex-col gap-1">
        {label ? <span className="hud-label">{label}</span> : null}
        <input
          ref={ref}
          id={id}
          {...rest}
          className={cn(
            "hud-clip bg-hud-bg/60 px-3 py-2",
            "border border-hud-cyan-dim font-mono text-sm text-hud-text",
            "placeholder:text-hud-text-dim/60",
            "focus:border-hud-cyan focus:outline-none focus:shadow-hud-glow",
            "transition-all duration-150",
            className,
          )}
        />
      </label>
    );
  },
);
