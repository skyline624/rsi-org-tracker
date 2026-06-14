"use client";
import Link from "next/link";
import { HudBadge } from "@/components/hud/HudBadge";
import {
  HudDataGrid,
  type HudColumn,
} from "@/components/hud/HudDataGrid";
import type { OrganizationMemberDto } from "@/lib/api/types";
import { formatDate, formatRelative } from "@/lib/utils/format";

export function UserOrgsTable({ rows }: { rows: OrganizationMemberDto[] }) {
  const columns: HudColumn<OrganizationMemberDto>[] = [
    {
      key: "status",
      header: "STATUS",
      width: "w-24",
      sortable: true,
      // Active rows sort to the top (true > false numerically once we coerce).
      sortValue: (m) => (m.isActive ? 0 : 1),
      render: (m) =>
        m.isActive ? (
          <HudBadge tone="green">active</HudBadge>
        ) : (
          <HudBadge tone="dim">left</HudBadge>
        ),
    },
    {
      key: "org",
      header: "ORG",
      width: "flex-1",
      sortable: true,
      sortValue: (m) => (m.orgName ?? m.orgSid ?? "").toLowerCase(),
      render: (m) => (
        <Link
          href={`/orgs/${m.orgSid}`}
          className="flex flex-col text-hud-cyan hover:text-hud-orange"
        >
          <span className="font-semibold">{m.orgName ?? m.orgSid}</span>
          {m.orgName && (
            <span className="font-mono text-[10px] uppercase tracking-wider text-hud-text-dim">
              {m.orgSid}
            </span>
          )}
        </Link>
      ),
    },
    {
      key: "role",
      header: "ROLE",
      width: "flex-1",
      sortable: true,
      sortValue: (m) => (m.roles?.[0] ?? m.rank ?? "").toLowerCase(),
      // Prefer the cleaned roles array (parsed from RolesJson). Fall back to
      // the raw Rank only if no role survived parsing — Rank often contains
      // the scraped column header ("Roles") rather than the real role.
      render: (m) => {
        const roles = m.roles?.filter((r) => r && r.toLowerCase() !== "roles") ?? [];
        if (roles.length > 0) return roles.join(", ");
        if (m.rank && m.rank.toLowerCase() !== "roles") return m.rank;
        return "—";
      },
    },
    {
      key: "since",
      header: "MEMBER SINCE",
      width: "w-28",
      align: "right",
      sortable: true,
      // Unknown first-seen sorts last (oldest-known at the bottom otherwise).
      sortValue: (m) => (m.memberSince ? new Date(m.memberSince).getTime() : Number.MAX_SAFE_INTEGER),
      render: (m) => (m.memberSince ? formatDate(m.memberSince) : "—"),
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
      // Active rows on top, then most-recent first.
      defaultSort={{ key: "status", dir: "asc" }}
      paginated
      pageSizeOptions={[10, 25, 50, 100, 0]}
      defaultPageSize={25}
    />
  );
}
