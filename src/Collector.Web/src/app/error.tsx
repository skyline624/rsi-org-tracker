"use client";

import { HudPanel } from "@/components/hud/HudPanel";

/**
 * A page failed while rendering (API down, unexpected answer). The error message is
 * not shown: it may carry server details. The digest ties it to the server log.
 */
export default function PageError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <div className="flex flex-col gap-6">
      <div>
        <div className="hud-label">— UEE::FAULT</div>
        <h1 className="mt-1 font-display text-3xl">Something failed</h1>
      </div>
      <HudPanel label="TRANSMISSION ERROR" accent="red">
        <p className="font-mono text-xs text-hud-text-dim">
          This page could not be loaded. The data service may be restarting; try again in
          a moment.
        </p>
        {error.digest && (
          <p className="mt-2 font-mono text-[10px] text-hud-text-dim">REF {error.digest}</p>
        )}
        <button
          onClick={reset}
          className="hud-clip mt-4 border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10"
        >
          Retry
        </button>
      </HudPanel>
    </div>
  );
}
