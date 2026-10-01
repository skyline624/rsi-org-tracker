import Link from "next/link";
import type { DiscordRoleDto } from "@/lib/api/types";
import { cleanDiscordText, reconciliationBadge } from "@/lib/discord/format";
import { MAX_SEARCH_LENGTH, RECONCILIATIONS, tabHref, type MemberFilters } from "@/lib/discord/params";

export const FILTER_FIELD_CLASS =
  "hud-clip border border-hud-cyan-dim bg-hud-bg px-2 py-1.5 font-mono text-xs text-hud-text [color-scheme:dark] focus:border-hud-cyan focus:outline-none";

export const FILTER_BUTTON_CLASS =
  "hud-clip border border-hud-cyan px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-cyan hover:bg-hud-cyan/10";

interface MemberFilterFormProps {
  guildId: string;
  filters: MemberFilters;
  /** Rank roles offered in the rank filter (not deleted). */
  rankRoles: DiscordRoleDto[];
  /** The reconciliation filter only means something for a mapped server. */
  mapped: boolean;
}

/** GET form of the members tab: filters live in the URL, so a filtered page can be shared. */
export function MemberFilterForm({ guildId, filters, rankRoles, mapped }: MemberFilterFormProps) {
  return (
    <form action={`/discord/${encodeURIComponent(guildId)}`} method="get" className="mb-4 flex flex-wrap items-end gap-2">
      <label className="flex flex-col gap-1">
        <span className="hud-label">STATUT</span>
        <select name="status" defaultValue={filters.status} className={FILTER_FIELD_CLASS}>
          <option value="active">présents</option>
          <option value="former">partis</option>
          <option value="all">tous</option>
        </select>
      </label>
      <label className="flex flex-col gap-1">
        <span className="hud-label">RECHERCHE</span>
        <input
          name="search"
          type="search"
          defaultValue={filters.search ?? ""}
          maxLength={MAX_SEARCH_LENGTH}
          placeholder="nom, pseudo…"
          className={FILTER_FIELD_CLASS}
        />
      </label>
      <label className="flex flex-col gap-1">
        <span className="hud-label">RANG</span>
        <select name="rankRoleId" defaultValue={filters.rankRoleId ?? ""} className={`${FILTER_FIELD_CLASS} max-w-[14rem]`}>
          <option value="">tous les rangs</option>
          {rankRoles.map((role) => (
            <option key={role.roleId} value={role.roleId}>
              {cleanDiscordText(role.name) ?? role.roleId}
            </option>
          ))}
        </select>
      </label>
      {mapped && (
        <label className="flex flex-col gap-1">
          <span className="hud-label">RECOUPEMENT</span>
          <select name="reconciliation" defaultValue={filters.reconciliation ?? ""} className={FILTER_FIELD_CLASS}>
            <option value="">tous</option>
            {RECONCILIATIONS.map((status) => (
              <option key={status} value={status}>
                {reconciliationBadge(status)?.label ?? status}
              </option>
            ))}
          </select>
        </label>
      )}
      <button type="submit" className={FILTER_BUTTON_CLASS}>
        FILTRER
      </button>
      <Link
        href={tabHref(guildId, "members")}
        className="px-2 py-1.5 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-cyan"
      >
        effacer
      </Link>
    </form>
  );
}
