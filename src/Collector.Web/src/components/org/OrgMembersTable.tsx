"use client";
import Link from "next/link";
import {
  HudDataGrid,
  type HudColumn,
} from "@/components/hud/HudDataGrid";
import type { OrganizationMemberDto } from "@/lib/api/types";
import { formatRelative } from "@/lib/utils/format";

export function OrgMembersTable({
  rows,
}: {
  rows: OrganizationMemberDto[];
}) {
  const columns: HudColumn<OrganizationMemberDto>[] = [
    {
      key: "handle",
      header: "HANDLE",
      width: "flex-1",
      sortable: true,
      sortValue: (m) => m.userHandle.toLowerCase(),
      render: (m) => (
        <Link
          href={`/users/${m.userHandle}`}
          className="text-hud-cyan hover:text-hud-orange"
        >
          {m.userHandle}
        </Link>
      ),
    },
    {
      key: "name",
      header: "DISPLAY NAME",
      width: "flex-1",
      sortable: true,
      sortValue: (m) => (m.displayName ?? "").toLowerCase(),
      render: (m) => m.displayName ?? "—",
    },
    {
      key: "rank",
      header: "RANK",
      width: "w-28",
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
      rowKey={(m) => m.userHandle}
      empty="No active members in snapshot."
      paginated
      pageSizeOptions={[10, 25, 50, 100, 0]}
      defaultPageSize={25}
    />
  );
}
