"use client";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { DiscordSyncDto } from "@/lib/api/types";
import { formatUtc, syncBadges } from "@/lib/discord/format";
import { formatNumber } from "@/lib/utils/format";

/** The upload journal: who, when, how, how many, and the complete / partial / mass-departure / baseline badges. */
export function SyncsTable({ rows }: { rows: DiscordSyncDto[] }) {
  const columns: HudColumn<DiscordSyncDto>[] = [
    {
      key: "received",
      header: "REÇU",
      width: "w-44",
      render: (s) => (
        <span className="flex flex-col">
          <span>{formatUtc(s.receivedAt)}</span>
          <span className="text-[10px] text-hud-text-dim">collecte {formatUtc(s.collectedAt)}</span>
        </span>
      ),
    },
    {
      key: "by",
      header: "PAR",
      width: "w-32 min-w-0",
      render: (s) => <span className="block truncate">{s.submittedBy}</span>,
    },
    { key: "method", header: "MÉTHODE", width: "w-32", render: (s) => s.method },
    {
      key: "members",
      header: "MEMBRES",
      width: "w-32",
      align: "right",
      render: (s) =>
        s.expectedCount === null
          ? formatNumber(s.collectedCount)
          : `${formatNumber(s.collectedCount)} / ${formatNumber(s.expectedCount)}`,
    },
    { key: "optedOut", header: "EXCLUS", width: "w-20", align: "right", render: (s) => formatNumber(s.optedOutCount) },
    { key: "events", header: "ÉVÉNEMENTS", width: "w-24", align: "right", render: (s) => formatNumber(s.eventCount) },
    {
      key: "state",
      header: "ÉTAT",
      width: "min-w-0 flex-1",
      render: (s) => (
        <span className="flex flex-wrap gap-1">
          {syncBadges(s).map((b) => (
            <HudBadge key={b.label} tone={b.tone}>
              {b.label}
            </HudBadge>
          ))}
        </span>
      ),
    },
    { key: "version", header: "PLUGIN", width: "w-20", render: (s) => s.pluginVersion },
  ];

  return <HudDataGrid minWidth={1088} columns={columns} rows={rows} rowKey={(s) => String(s.id)} empty="Aucun envoi." />;
}
