"use client";
import Link from "next/link";
import { HudBadge } from "@/components/hud/HudBadge";
import {
  HudDataGrid,
  type HudColumn,
} from "@/components/hud/HudDataGrid";
import type { OrganizationDto } from "@/lib/api/types";
import { formatNumber } from "@/lib/utils/format";

export function OrgsTable({ rows }: { rows: OrganizationDto[] }) {
  const columns: HudColumn<OrganizationDto>[] = [
    {
      key: "sid",
      header: "SID",
      width: "w-32",
      sortable: true,
      sortValue: (r) => r.sid,
      render: (r) => (
        <Link
          href={`/orgs/${r.sid}`}
          className="text-hud-cyan hover:text-hud-orange"
        >
          {r.sid}
        </Link>
      ),
    },
    {
      key: "name",
      header: "NAME",
      width: "flex-1",
      sortable: true,
      sortValue: (r) => r.name,
      render: (r) => <span className="text-hud-text">{r.name}</span>,
    },
    {
      key: "archetype",
      header: "TYPE",
      width: "w-28",
      sortable: true,
      sortValue: (r) => r.archetype ?? "",
      render: (r) => r.archetype ?? "—",
    },
    {
      key: "lang",
      header: "LANG",
      width: "w-16",
      sortable: true,
      sortValue: (r) => r.lang ?? "",
      render: (r) => r.lang ?? "—",
    },
    {
      key: "members",
      header: "MEMBERS",
      width: "w-24",
      align: "right",
      sortable: true,
      sortValue: (r) => r.membersCount,
      render: (r) => formatNumber(r.membersCount),
    },
    {
      key: "recruiting",
      header: "RECR",
      width: "w-20",
      align: "center",
      sortable: true,
      // boolean → number so asc shows false first, desc shows true first
      sortValue: (r) => (r.recruiting ? 1 : r.recruiting === false ? 0 : -1),
      render: (r) =>
        r.recruiting ? (
          <HudBadge tone="green">YES</HudBadge>
        ) : r.recruiting === false ? (
          <HudBadge tone="dim">NO</HudBadge>
        ) : (
          <span className="text-hud-text-dim">—</span>
        ),
    },
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(r) => r.sid}
      empty="No orgs matched that query."
    />
  );
}
