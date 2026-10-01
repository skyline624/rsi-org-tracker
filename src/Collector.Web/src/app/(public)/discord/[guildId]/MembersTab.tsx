import { HudPanel } from "@/components/hud/HudPanel";
import { Pagination } from "@/components/layout/Pagination";
import { getDiscordMembers } from "@/lib/api/endpoints";
import type { DiscordGuildDetailDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { MEMBERS_PAGE_SIZE, parseMemberFilters, type SearchParams } from "@/lib/discord/params";
import { formatNumber } from "@/lib/utils/format";
import { DiscordMembersTable } from "./DiscordMembersTable";
import { loadOrNotFound } from "./load";
import { MemberFilterForm } from "./MemberFilterForm";

interface MembersTabProps {
  ctx: AuthCtx;
  guild: DiscordGuildDetailDto;
  searchParams: SearchParams;
}

/** Members paged by the API, filtered through the URL (status, search, rank, reconciliation). */
export async function MembersTab({ ctx, guild, searchParams }: MembersTabProps) {
  const filters = parseMemberFilters(searchParams);
  const mapped = guild.orgSid !== null;
  const page = await loadOrNotFound(
    getDiscordMembers(ctx, guild.guildId, {
      status: filters.status,
      search: filters.search,
      rankRoleId: filters.rankRoleId,
      // The API ignores it for an unmapped server: not sent either.
      reconciliation: mapped ? filters.reconciliation : undefined,
      page: filters.page,
      pageSize: MEMBERS_PAGE_SIZE,
    }),
  );
  const rankRoles = guild.roles.filter((role) => role.isRank && !role.deleted);

  return (
    <HudPanel label={`MEMBRES · ${formatNumber(page.total)}`}>
      <MemberFilterForm guildId={guild.guildId} filters={filters} rankRoles={rankRoles} mapped={mapped} />
      <DiscordMembersTable rows={page.items} guildId={guild.guildId} />
      {page.totalPages > 1 && (
        <Pagination param="page" page={page.page} totalPages={page.totalPages} total={page.total} />
      )}
    </HudPanel>
  );
}
