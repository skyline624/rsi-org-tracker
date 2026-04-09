"use client";
import Link from "next/link";
import {
  HudDataGrid,
  type HudColumn,
} from "@/components/hud/HudDataGrid";
import type { OrganizationMemberDto } from "@/lib/api/types";
import { formatRelative } from "@/lib/utils/format";

export function UserOrgsTable({ rows }: { rows: OrganizationMemberDto[] }) {
  const columns: HudColumn<OrganizationMemberDto>[] = [
    {
      key: "sid",
      header: "SID",
      width: "w-28",
      sortable: true,
      sortValue: (m) => m.orgSid ?? "",
      render: (m) => (
        <Link
          href={`/orgs/${m.orgSid}`}
          className="text-hud-cyan hover:text-hud-orange"
        >
          {m.orgSid}
        </Link>
      ),
    },
    {
      key: "rank",
      header: "RANK",
      width: "flex-1",
      sortable: true,
      sortValue: (m) => (m.rank ?? "").toLowerCase(),
      render: (m) => m.rank ?? "—",
    },
    {
      key: "last",
      header: "LAST SEEN",
      width: "w-28",
      align: "right",
      sortable: true,
      sortValue: (m) => new Date(m.timestamp).getTime(),
      render: (m) => formatRelative(m.timestamp),
    },
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(m) => `${m.orgSid}-${m.timestamp}`}
      empty="Citizen is not tied to any org in our index."
      paginated
      pageSizeOptions={[10, 25, 50, 100, 0]}
      defaultPageSize={25}
    />
  );
}
