"use client";
import Link from "next/link";
import {
  HudDataGrid,
  type HudColumn,
} from "@/components/hud/HudDataGrid";
import type { OrganizationMemberDto } from "@/lib/api/types";
import { formatRelative } from "@/lib/utils/format";

/** One server page of a roster, already in handle order. */
export function OrgMembersTable({
  rows,
  empty = "No members.",
}: {
  rows: OrganizationMemberDto[];
  empty?: string;
}) {
  const columns: HudColumn<OrganizationMemberDto>[] = [
    {
      key: "handle",
      header: "HANDLE",
      width: "flex-1",
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
      render: (m) => m.displayName ?? "—",
    },
    {
      key: "rank",
      header: "RANK",
      width: "w-28",
      render: (m) => m.rank ?? "—",
    },
    {
      key: "last",
      header: "LAST SEEN",
      width: "w-28",
      align: "right",
      render: (m) => formatRelative(m.timestamp),
    },
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(m) => m.userHandle}
      empty={empty}
    />
  );
}
