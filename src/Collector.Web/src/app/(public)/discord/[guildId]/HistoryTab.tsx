import Link from "next/link";
import { DiscordText } from "@/components/discord/DiscordText";
import { RoleChanges } from "@/components/discord/RoleChanges";
import { ValueChange } from "@/components/discord/ValueChange";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudPanel } from "@/components/hud/HudPanel";
import { getDiscordEvents } from "@/lib/api/endpoints";
import type { DiscordEventDto } from "@/lib/api/types";
import type { AuthCtx } from "@/lib/auth/server-api";
import { eventTone, eventTypeLabel, eventWhen, rankChangeText } from "@/lib/discord/format";
import { EVENT_TYPES, HISTORY_LIMIT, parseEventFilters, tabHref, type SearchParams } from "@/lib/discord/params";
import { loadOrNotFound } from "./load";
import { FILTER_BUTTON_CLASS, FILTER_FIELD_CLASS } from "./MemberFilterForm";

const NAME_CHANGES = new Set(["nick_changed", "username_changed", "global_name_changed"]);

/** What changed: « Rang : X → Y », the roles gained and lost, or the old and new name. */
function EventDetail({ event }: { event: DiscordEventDto }) {
  return (
    <>
      {event.rankChange && (
        <span className="inline-flex min-w-0 max-w-[24rem] text-hud-orange">
          <DiscordText value={rankChangeText(event.rankChange)} />
        </span>
      )}
      {event.type === "roles_changed" && <RoleChanges oldValue={event.oldValue} newValue={event.newValue} />}
      {NAME_CHANGES.has(event.type) && <ValueChange oldValue={event.oldValue} newValue={event.newValue} />}
    </>
  );
}

interface HistoryTabProps {
  ctx: AuthCtx;
  guildId: string;
  searchParams: SearchParams;
}

/** The server's history, newest first, with who sent the upload that recorded each event. */
export async function HistoryTab({ ctx, guildId, searchParams }: HistoryTabProps) {
  const filters = parseEventFilters(searchParams);
  const events = await loadOrNotFound(
    getDiscordEvents(ctx, guildId, { type: filters.type, userId: filters.userId, limit: HISTORY_LIMIT }),
  );
  const filtered = filters.type !== undefined || filters.userId !== undefined;

  return (
    <HudPanel label={`HISTORIQUE · ${events.length} ÉVÉNEMENTS`} accent="orange">
      <form action={`/discord/${encodeURIComponent(guildId)}`} method="get" className="mb-4 flex flex-wrap items-end gap-2">
        <input type="hidden" name="tab" value="history" />
        {filters.userId && <input type="hidden" name="userId" value={filters.userId} />}
        <label className="flex flex-col gap-1">
          <span className="hud-label">TYPE</span>
          <select name="type" defaultValue={filters.type ?? ""} className={FILTER_FIELD_CLASS}>
            <option value="">tous</option>
            {EVENT_TYPES.map((type) => (
              <option key={type} value={type}>
                {eventTypeLabel(type)}
              </option>
            ))}
          </select>
        </label>
        <button type="submit" className={FILTER_BUTTON_CLASS}>
          FILTRER
        </button>
        {filtered && (
          <Link
            href={tabHref(guildId, "history")}
            className="px-2 py-1.5 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-cyan"
          >
            {"tout l'historique"}
          </Link>
        )}
      </form>
      <p className="mb-3 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
        {filters.userId ? `Compte ${filters.userId} seulement · ` : ""}
        {`${HISTORY_LIMIT} derniers événements au plus.`}
      </p>
      {events.length === 0 ? (
        <div className="py-6 text-center font-mono text-xs text-hud-text-dim">— aucun événement —</div>
      ) : (
        <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
          {events.map((event) => (
            <li key={event.id} className="flex flex-col gap-1 py-2">
              <div className="flex min-w-0 flex-wrap items-center gap-2">
                <HudBadge tone={eventTone(event.type)}>{eventTypeLabel(event.type)}</HudBadge>
                <Link
                  href={tabHref(guildId, "history", { userId: event.discordUserId })}
                  className="inline-flex min-w-0 max-w-[16rem] text-hud-cyan hover:text-hud-orange"
                >
                  <DiscordText value={event.username} fallback={event.discordUserId} />
                </Link>
                <EventDetail event={event} />
              </div>
              <div className="text-[10px] text-hud-text-dim">
                {eventWhen(event)}
                {event.submittedBy ? ` · envoi de ${event.submittedBy}` : ""}
              </div>
            </li>
          ))}
        </ul>
      )}
    </HudPanel>
  );
}
