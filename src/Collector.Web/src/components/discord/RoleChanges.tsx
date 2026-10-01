import { rolesDiff } from "@/lib/discord/format";
import { DiscordText } from "./DiscordText";

/** Roles gained (+) and lost (−) by a roles_changed event, with the names they had then. */
export function RoleChanges({ oldValue, newValue }: { oldValue: string | null; newValue: string | null }) {
  const { added, removed } = rolesDiff(oldValue, newValue);
  if (added.length === 0 && removed.length === 0) return null;
  return (
    <span className="flex min-w-0 flex-wrap items-center gap-2">
      {added.map((role) => (
        <span key={`+${role.id}`} className="inline-flex min-w-0 max-w-[12rem] items-center text-hud-green">
          +<DiscordText value={role.name} fallback={role.id} />
        </span>
      ))}
      {removed.map((role) => (
        <span key={`-${role.id}`} className="inline-flex min-w-0 max-w-[12rem] items-center text-hud-red">
          −<DiscordText value={role.name} fallback={role.id} />
        </span>
      ))}
    </span>
  );
}
