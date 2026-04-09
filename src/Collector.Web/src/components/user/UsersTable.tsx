"use client";
import Link from "next/link";
import {
  HudDataGrid,
  type HudColumn,
} from "@/components/hud/HudDataGrid";
import type { UserProfileDto } from "@/lib/api/types";
import { formatNumber, formatRelative } from "@/lib/utils/format";

export function UsersTable({ rows }: { rows: UserProfileDto[] }) {
  const columns: HudColumn<UserProfileDto>[] = [
    {
      key: "handle",
      header: "HANDLE",
      width: "flex-1",
      sortable: true,
      sortValue: (u) => u.userHandle.toLowerCase(),
      render: (u) => (
        <Link
          href={`/users/${u.userHandle}`}
          className="text-hud-cyan hover:text-hud-orange"
        >
          {u.userHandle}
        </Link>
      ),
    },
    {
      key: "display",
      header: "DISPLAY NAME",
      width: "flex-1",
      sortable: true,
      sortValue: (u) => (u.displayName ?? "").toLowerCase(),
      render: (u) => u.displayName ?? "—",
    },
    {
      key: "cid",
      header: "CITIZEN_ID",
      width: "w-28",
      align: "right",
      sortable: true,
      sortValue: (u) => u.citizenId,
      render: (u) => (
        <span className="text-hud-text-dim">
          #{formatNumber(u.citizenId)}
        </span>
      ),
    },
    {
      key: "location",
      header: "LOCATION",
      width: "w-28",
      sortable: true,
      sortValue: (u) => (u.location ?? "").toLowerCase(),
      render: (u) => u.location ?? "—",
    },
    {
      key: "updated",
      header: "UPDATED",
      width: "w-24",
      align: "right",
      sortable: true,
      // Store as timestamp so asc = oldest first, desc = newest first.
      sortValue: (u) => new Date(u.updatedAt).getTime(),
      render: (u) => formatRelative(u.updatedAt),
    },
  ];

  return (
    <HudDataGrid
      columns={columns}
      rows={rows}
      rowKey={(u) => u.userHandle}
      empty="No citizens matched that query."
    />
  );
}
