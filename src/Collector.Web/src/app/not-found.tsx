import Link from "next/link";
import { HudPanel } from "@/components/hud/HudPanel";

export default function NotFound() {
  return (
    <div className="flex flex-col gap-6">
      <div>
        <div className="hud-label">— UEE::404</div>
        <h1 className="mt-1 font-display text-3xl">Not found</h1>
      </div>
      <HudPanel label="SIGNAL LOST" accent="orange">
        <p className="font-mono text-xs text-hud-text-dim">
          Nothing in the index answers to this address: the organization or citizen may
          never have been collected, or the link is wrong.
        </p>
        <Link
          href="/"
          className="mt-4 inline-block font-mono text-xs uppercase tracking-wider text-hud-cyan hover:text-hud-orange"
        >
          ← Back to the overview
        </Link>
      </HudPanel>
    </div>
  );
}
