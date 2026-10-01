import { safeRoleColor } from "@/lib/discord/format";

/** The colour of a Discord role, as a dot; hollow for a role without colour. */
export function RoleDot({ color }: { color: string | null }) {
  const safe = safeRoleColor(color);
  return (
    <span
      aria-hidden
      className="inline-block h-2 w-2 shrink-0 rounded-full border border-hud-text-dim/40"
      style={safe ? { backgroundColor: safe, borderColor: safe } : undefined}
    />
  );
}
