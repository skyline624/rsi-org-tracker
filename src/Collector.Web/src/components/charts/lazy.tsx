"use client";

import dynamic from "next/dynamic";

/**
 * Charts are loaded on demand: recharts (~100 kB) only reaches the browser on pages
 * that draw one, and never runs on the server. Pages import charts from here.
 */
function ChartPlaceholder({ height }: { height: number }) {
  return (
    <div
      style={{ height }}
      className="flex w-full items-center justify-center font-mono text-xs uppercase tracking-wider text-hud-text-dim"
    >
      — loading chart —
    </div>
  );
}

export const TimelineChart = dynamic(
  () => import("./TimelineChart").then((m) => m.TimelineChart),
  { ssr: false, loading: () => <ChartPlaceholder height={220} /> },
);

export const ArchetypeDonut = dynamic(
  () => import("./ArchetypeDonut").then((m) => m.ArchetypeDonut),
  { ssr: false, loading: () => <ChartPlaceholder height={260} /> },
);

export const MemberActivityBar = dynamic(
  () => import("./MemberActivityBar").then((m) => m.MemberActivityBar),
  { ssr: false, loading: () => <ChartPlaceholder height={260} /> },
);
