"use client";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { motion, AnimatePresence } from "framer-motion";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { apiGet } from "@/lib/api/client";
import type { ChangeEventDto, ChangeSummaryDto } from "@/lib/api/types";
import { formatRelative } from "@/lib/utils/format";

function useChangesFeed() {
  return useQuery({
    queryKey: ["changes-feed"],
    queryFn: () =>
      apiGet<ChangeEventDto[]>("/api/changes", { limit: 100 }),
    refetchInterval: 30_000,
    refetchOnWindowFocus: true,
  });
}

function useChangesSummary() {
  return useQuery({
    queryKey: ["changes-summary"],
    queryFn: () =>
      apiGet<ChangeSummaryDto[]>("/api/changes/summary", { days: 30 }),
    refetchInterval: 60_000,
  });
}

export default function ChangesPage() {
  const feed = useChangesFeed();
  const summary = useChangesSummary();

  return (
    <div className="flex flex-col gap-8">
      <header>
        <div className="hud-label">— UEE::LIVE_CHANGELOG</div>
        <h1 className="mt-1 font-display text-3xl">Change Transmissions</h1>
        <p className="mt-1 font-mono text-xs text-hud-text-dim">
          Polling every 30 seconds. Latest 100 events across the fleet.
        </p>
      </header>

      {/* Summary */}
      <section>
        <HudPanel label="LAST 30 DAYS · BY TYPE">
          {summary.isLoading ? (
            <div className="py-4 text-center font-mono text-xs text-hud-text-dim">
              — loading —
            </div>
          ) : summary.data && summary.data.length > 0 ? (
            <div className="flex flex-wrap gap-3">
              {summary.data.map((s) => (
                <div
                  key={s.changeType}
                  className="flex items-center gap-2 border border-hud-cyan-dim px-3 py-1.5"
                >
                  <HudBadge
                    tone={
                      s.changeType.includes("joined")
                        ? "green"
                        : s.changeType.includes("left")
                          ? "red"
                          : "orange"
                    }
                  >
                    {s.changeType.replace(/_/g, " ")}
                  </HudBadge>
                  <span className="font-mono text-sm tabular-nums text-hud-cyan">
                    {s.count.toLocaleString()}
                  </span>
                </div>
              ))}
            </div>
          ) : (
            <div className="py-4 text-center font-mono text-xs text-hud-text-dim">
              — no summary —
            </div>
          )}
        </HudPanel>
      </section>

      {/* Feed */}
      <section>
        <HudPanel label="LIVE FEED" accent="cyan">
          {feed.isLoading ? (
            <div className="py-10 text-center font-mono text-xs text-hud-text-dim">
              — receiving telemetry —
            </div>
          ) : feed.isError ? (
            <div className="py-10 text-center font-mono text-xs text-hud-red">
              — transmission error —
            </div>
          ) : (
            <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
              <AnimatePresence initial={false}>
                {(feed.data ?? []).map((c) => (
                  <motion.li
                    key={c.id}
                    initial={{ opacity: 0, x: -16 }}
                    animate={{ opacity: 1, x: 0 }}
                    exit={{ opacity: 0 }}
                    transition={{ duration: 0.2, ease: [0.22, 1, 0.36, 1] }}
                    className="flex items-center gap-3 py-2"
                  >
                    <HudBadge
                      tone={
                        c.changeType.includes("joined")
                          ? "green"
                          : c.changeType.includes("left")
                            ? "red"
                            : "orange"
                      }
                    >
                      {c.changeType.replace(/_/g, " ")}
                    </HudBadge>
                    <span className="flex-1 truncate text-hud-text">
                      {c.entityType}
                      <span className="text-hud-text-dim">://</span>
                      {c.userHandle ? (
                        <Link
                          href={`/users/${c.userHandle}`}
                          className="text-hud-cyan hover:text-hud-orange"
                        >
                          {c.userHandle}
                        </Link>
                      ) : (
                        <span className="text-hud-cyan">{c.entityId}</span>
                      )}
                    </span>
                    {c.orgSid && (
                      <Link
                        href={`/orgs/${c.orgSid}`}
                        className="text-hud-text-dim hover:text-hud-cyan"
                      >
                        @ {c.orgSid}
                      </Link>
                    )}
                    <time className="text-hud-text-dim">
                      {formatRelative(c.timestamp)}
                    </time>
                  </motion.li>
                ))}
              </AnimatePresence>
            </ul>
          )}
        </HudPanel>
      </section>
    </div>
  );
}
