"use client";

import Link from "next/link";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import type { ChangeEventDto } from "@/lib/api/types";
import { formatRelative } from "@/lib/utils/format";

interface ChangesTableProps {
  rows: ChangeEventDto[];
  defaultPageSize?: number;
  pageSizeOptions?: number[];
}

interface EntityTarget {
  href: string | null;
  label: string;
}

function entityTarget(c: ChangeEventDto): EntityTarget {
  // Prefer userHandle (always populated for user/member events with a known
  // handle) — entityId for "user" rows is the citizen_id (a number) and for
  // "organization" rows is the org sid.
  if (c.userHandle) return { href: `/users/${c.userHandle}`, label: c.userHandle };
  if (c.entityType === "organization") {
    return { href: `/orgs/${c.entityId}`, label: c.entityId };
  }
  if (c.entityType === "member") {
    return { href: `/users/${c.entityId}`, label: c.entityId };
  }
  return { href: null, label: c.entityId };
}

function badgeTone(t: string): "green" | "red" | "orange" {
  if (t.includes("joined")) return "green";
  if (t.includes("left")) return "red";
  return "orange";
}

export function ChangesTable({
  rows,
  defaultPageSize = 25,
  pageSizeOptions = [10, 25, 50, 100, 0],
}: ChangesTableProps) {
  const columns: HudColumn<ChangeEventDto>[] = [
    {
      key: "type",
      header: "TYPE",
      width: "w-44",
      sortable: true,
      sortValue: (c) => c.changeType,
      render: (c) => (
        <HudBadge tone={badgeTone(c.changeType)}>
          {c.changeType.replace(/_/g, " ")}
        </HudBadge>
      ),
    },
    {
      key: "entity",
      header: "ENTITY",
      width: "flex-1",
      sortable: true,
      sortValue: (c) => entityTarget(c).label.toLowerCase(),
      render: (c) => {
        const t = entityTarget(c);
        return (
          <span className="truncate">
            <span className="text-hud-text-dim">{c.entityType}://</span>
            {t.href ? (
              <Link
                href={t.href}
                className="text-hud-cyan hover:text-hud-orange"
              >
                {t.label}
              </Link>
            ) : (
              <span className="text-hud-cyan">{t.label}</span>
            )}
          </span>
        );
      },
    },
    {
      key: "org",
      header: "ORG",
      width: "w-28",
      sortable: true,
      sortValue: (c) => c.orgSid ?? "",
      render: (c) =>
        c.orgSid ? (
          <Link
            href={`/orgs/${c.orgSid}`}
            className="text-hud-cyan hover:text-hud-orange"
          >
            {c.orgSid}
          </Link>
        ) : (
          <span className="text-hud-text-dim">—</span>
        ),
    },
    {
      key: "time",
      header: "WHEN",
      width: "w-28",
      align: "right",
      sortable: true,
      sortValue: (c) => new Date(c.timestamp).getTime(),
      render: (c) => (
        <time className="text-hud-text-dim">{formatRelative(c.timestamp)}</time>
      ),
    },
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(c) => String(c.id)}
      empty="— awaiting telemetry —"
      defaultSort={{ key: "time", dir: "desc" }}
      paginated
      pageSizeOptions={pageSizeOptions}
      defaultPageSize={defaultPageSize}
    />
  );
}
