"use client";
import Link from "next/link";
import {
  HudDataGrid,
  type HudColumn,
} from "@/components/hud/HudDataGrid";
import type { OrganizationTopDto } from "@/lib/api/types";
import { formatNumber } from "@/lib/utils/format";

type Row = OrganizationTopDto & { __rank: number };

export function TopOrgsTable({ rows }: { rows: OrganizationTopDto[] }) {
  const withRank: Row[] = rows.map((o, i) => ({ ...o, __rank: i + 1 }));

  const columns: HudColumn<Row>[] = [
    {
      key: "rank",
      header: "#",
      width: "w-8",
      align: "right",
      sortable: true,
      sortValue: (r) => r.__rank,
      render: (r) => r.__rank.toString(),
    },
    {
      key: "sid",
      header: "SID",
      width: "w-28",
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
      sortValue: (r) => r.name.toLowerCase(),
      render: (r) => r.name,
    },
    {
      key: "arch",
      header: "TYPE",
      width: "w-24",
      sortable: true,
      sortValue: (r) => (r.archetype ?? "").toLowerCase(),
      render: (r) => r.archetype ?? "—",
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
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={withRank}
      rowKey={(r) => r.sid}
      empty="No top orgs data yet."
      defaultSort={{ key: "rank", dir: "asc" }}
    />
  );
}
